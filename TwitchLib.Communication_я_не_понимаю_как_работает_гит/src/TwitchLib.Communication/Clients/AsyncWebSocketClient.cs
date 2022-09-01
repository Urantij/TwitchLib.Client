using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Communication.Enums;
using TwitchLib.Communication.Events;
using TwitchLib.Communication.Interfaces;
using TwitchLib.Communication.Models;
using TwitchLib.Communication.Services;

namespace TwitchLib.Communication.Clients
{
    public class AsyncWebSocketClient : IAsyncClient
    {
        private int NotConnectedCounter;
        public TimeSpan DefaultKeepAliveInterval { get; set; }
        public int SendQueueLength => _throttlers.SendQueue.Count;
        public int WhisperQueueLength => _throttlers.WhisperQueue.Count;
        public bool IsConnected => Client?.State == WebSocketState.Open;
        public IClientOptions Options { get; }
        public ClientWebSocket Client { get; private set; }

        public event EventHandler<OnConnectedEventArgs> OnConnected;
        public event EventHandler<OnDataEventArgs> OnData;
        public event EventHandler<OnDisconnectedEventArgs> OnDisconnected;
        public event EventHandler<OnErrorEventArgs> OnError;
        public event EventHandler<OnFatalErrorEventArgs> OnFatality;
        public event EventHandler<OnMessageEventArgs> OnMessage;
        public event EventHandler<OnMessageThrottledEventArgs> OnMessageThrottled;
        public event EventHandler<OnWhisperThrottledEventArgs> OnWhisperThrottled;
        public event EventHandler<OnSendFailedEventArgs> OnSendFailed;
        public event EventHandler<OnStateChangedEventArgs> OnStateChanged;
        public event EventHandler<OnReconnectedEventArgs> OnReconnected;

        private string Url { get; }
        private readonly Throttlers _throttlers;
        private CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private bool _stopServices;
        private bool _networkServicesRunning;
        private Task[] _networkTasks;
        private Task _monitorTask;

        public AsyncWebSocketClient(IClientOptions options = null)
        {
            Options = options ?? new ClientOptions();

            switch (Options.ClientType)
            {
                case ClientType.Chat:
                    Url = Options.UseSsl ? "wss://irc-ws.chat.twitch.tv:443" : "ws://irc-ws.chat.twitch.tv:80";
                    break;
                case ClientType.PubSub:
                    Url = Options.UseSsl ? "wss://pubsub-edge.twitch.tv:443" : "ws://pubsub-edge.twitch.tv:80";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _throttlers = new Throttlers(this, Options.ThrottlingPeriod, Options.WhisperThrottlingPeriod) { TokenSource = _tokenSource };
        }

        private void InitializeClient()
        {
            Client?.Abort();
            Client = new ClientWebSocket();

            if (_monitorTask == null)
            {
                _monitorTask = StartMonitorTask();
                return;
            }

            if (_monitorTask.IsCompleted) _monitorTask = StartMonitorTask();
        }

        public async Task<bool> OpenAsync()
        {
            try
            {
                if (IsConnected) return true;

                InitializeClient();
                await Client.ConnectAsync(new Uri(Url), _tokenSource.Token);
                if (!IsConnected) return await OpenAsync();

                await StartNetworkServicesAsync();
                return true;
            }
            catch (WebSocketException)
            {
                InitializeClient();
                return false;
            }
        }

        public async Task CloseAsync(bool callDisconnect = true)
        {
            Client?.Abort();
            _stopServices = callDisconnect;
            await CleanupServicesAsync();
            InitializeClient();
            OnDisconnected?.Invoke(this, new OnDisconnectedEventArgs());
        }

        public void Reconnect()
        {
            Task.Run(async () =>
            {
                await Task.Delay(20);
                await CloseAsync();
                if (await OpenAsync())
                {
                    OnReconnected?.Invoke(this, new OnReconnectedEventArgs());
                }
            });
        }

        public bool Send(string message)
        {
            try
            {
                if (!IsConnected || SendQueueLength >= Options.SendQueueCapacity)
                {
                    return false;
                }

                _throttlers.SendQueue.Add(new Tuple<DateTime, string>(DateTime.UtcNow, message));

                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                throw;
            }
        }

        public bool SendWhisper(string message)
        {
            try
            {
                if (!IsConnected || WhisperQueueLength >= Options.WhisperQueueCapacity)
                {
                    return false;
                }

                _throttlers.WhisperQueue.Add(new Tuple<DateTime, string>(DateTime.UtcNow, message));

                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                throw;
            }
        }

        private async Task StartNetworkServicesAsync()
        {
            _networkServicesRunning = true;
            _networkTasks = new[]
            {
                StartListenerTask(),
                _throttlers.StartSenderTask(),
                _throttlers.StartWhisperSenderTask()
            }.ToArray();

            if (!_networkTasks.Any(c => c.IsFaulted)) return;
            _networkServicesRunning = false;

            await CleanupServicesAsync();
        }

        public async Task SendAsync(byte[] message)
        {
            await Client.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, _tokenSource.Token);
        }

        private Task StartListenerTask()
        {
            return Task.Run(async () =>
            {
                var message = "";

                while (IsConnected && _networkServicesRunning)
                {
                    WebSocketReceiveResult result;
                    var buffer = new byte[1024];

                    try
                    {
                        result = await Client.ReceiveAsync(new ArraySegment<byte>(buffer), _tokenSource.Token);
                    }
                    catch
                    {
                        InitializeClient();
                        break;
                    }

                    if (result == null) continue;

                    switch (result.MessageType)
                    {
                        case WebSocketMessageType.Close:
                            await CloseAsync();
                            break;
                        case WebSocketMessageType.Text when !result.EndOfMessage:
                            message += Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                            continue;
                        case WebSocketMessageType.Text:
                            message += Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                            OnMessage?.Invoke(this, new OnMessageEventArgs() { Message = message });
                            break;
                        case WebSocketMessageType.Binary:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    message = "";
                }
            });
        }

        private Task StartMonitorTask()
        {
            return Task.Run(async () =>
            {
                var needsReconnect = false;
                var checkConnectedCounter = 0;
                try
                {
                    var lastState = IsConnected;
                    while (!_tokenSource.IsCancellationRequested)
                    {
                        if (lastState == IsConnected)
                        {
                            //Thread.Sleep(200);
                            await Task.Delay(200);

                            if (!IsConnected)
                                NotConnectedCounter++;
                            else
                                checkConnectedCounter++;

                            if (checkConnectedCounter >= 300) //Check every 60s for Response
                            {
                                Send("PING");
                                checkConnectedCounter = 0;
                            }

                            switch (NotConnectedCounter)
                            {
                                case 25: //Try Reconnect after 5s
                                case 75: //Try Reconnect after extra 10s
                                case 150: //Try Reconnect after extra 15s
                                case 300: //Try Reconnect after extra 30s
                                case 600: //Try Reconnect after extra 60s
                                    Reconnect();
                                    break;
                                default:
                                    {
                                        if (NotConnectedCounter >= 1200 && NotConnectedCounter % 600 == 0) //Try Reconnect after every 120s from this point
                                            Reconnect();
                                        break;
                                    }
                            }

                            if (NotConnectedCounter != 0 && IsConnected)
                                NotConnectedCounter = 0;

                            continue;
                        }
                        OnStateChanged?.Invoke(this, new OnStateChangedEventArgs { IsConnected = Client.State == WebSocketState.Open, WasConnected = lastState });

                        if (IsConnected)
                            OnConnected?.Invoke(this, new OnConnectedEventArgs());

                        if (!IsConnected && !_stopServices)
                        {
                            if (lastState && Options.ReconnectionPolicy != null && !Options.ReconnectionPolicy.AreAttemptsComplete())
                            {
                                needsReconnect = true;
                                break;
                            }

                            OnDisconnected?.Invoke(this, new OnDisconnectedEventArgs());
                            if (Client.CloseStatus != null && Client.CloseStatus != WebSocketCloseStatus.NormalClosure)
                                OnError?.Invoke(this, new OnErrorEventArgs { Exception = new Exception(Client.CloseStatus + " " + Client.CloseStatusDescription) });
                        }

                        lastState = IsConnected;
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                }

                if (needsReconnect && !_stopServices)
                    Reconnect();
            }, _tokenSource.Token);
        }

        private async Task CleanupServicesAsync()
        {
            _tokenSource.Cancel();
            _tokenSource = new CancellationTokenSource();
            _throttlers.TokenSource = _tokenSource;

            if (!_stopServices) return;
            if (!(_networkTasks?.Length > 0)) return;

            int slept = 0;
            while (_networkTasks.Any(t => !t.IsCompleted))
            {
                if (slept >= 15000)
                {
                    break;
                }

                await Task.Delay(1000);
                slept += 1000;
            }

            if (slept < 15000) return;

            OnFatality?.Invoke(this,
                new OnFatalErrorEventArgs
                {
                    Reason = "Fatal network error. Network services fail to shut down."
                });
            _stopServices = false;
            _throttlers.Reconnecting = false;
            _networkServicesRunning = false;
        }

        public void WhisperThrottled(OnWhisperThrottledEventArgs eventArgs)
        {
            OnWhisperThrottled?.Invoke(this, eventArgs);
        }

        public void MessageThrottled(OnMessageThrottledEventArgs eventArgs)
        {
            OnMessageThrottled?.Invoke(this, eventArgs);
        }

        public void SendFailed(OnSendFailedEventArgs eventArgs)
        {
            OnSendFailed?.Invoke(this, eventArgs);
        }

        public void Error(OnErrorEventArgs eventArgs)
        {
            OnError?.Invoke(this, eventArgs);
        }

        public void Dispose()
        {
            CloseAsync().ContinueWith((task) =>
            {
                _throttlers.ShouldDispose = true;
                _tokenSource.Cancel();
                //Thread.Sleep(500);
                _tokenSource.Dispose();
                Client?.Dispose();
                GC.Collect();
            });
        }
    }
}