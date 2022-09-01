using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Events;
using TwitchLib.Communication.Interfaces;

namespace TwitchLib.Communication.Services
{
    public class AsyncThrottlers
    {
        readonly SemaphoreSlim sendLocker = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim whisperLocker = new SemaphoreSlim(1, 1);

        readonly ConcurrentQueue<Tuple<DateTime, string>> SendQueue2 =
                new ConcurrentQueue<Tuple<DateTime, string>>();
        public int SendQueueCount => SendQueue2.Count;

        readonly ConcurrentQueue<Tuple<DateTime, string>> WhisperQueue2 =
                new ConcurrentQueue<Tuple<DateTime, string>>();
        public int WhisperQueueCount => WhisperQueue2.Count;

        public bool Reconnecting { get; set; } = false;
        public bool ShouldDispose { get; set; } = false;
        public CancellationTokenSource TokenSource { get; set; }
        public bool ResetThrottlerRunning;
        public bool ResetWhisperThrottlerRunning;
        public int SentCount = 0;
        public int WhispersSent = 0;
        public Task ResetThrottler;
        public Task ResetWhisperThrottler;

        private readonly TimeSpan _throttlingPeriod;
        private readonly TimeSpan _whisperThrottlingPeriod;
        private readonly IThrottleClient _client;

        public AsyncThrottlers(IThrottleClient client, TimeSpan throttlingPeriod, TimeSpan whisperThrottlingPeriod)
        {
            _throttlingPeriod = throttlingPeriod;
            _whisperThrottlingPeriod = whisperThrottlingPeriod;
            _client = client;
        }

        public void StartThrottlingWindowReset()
        {
            ResetThrottler = Task.Run(async () =>
            {
                ResetThrottlerRunning = true;
                while (!ShouldDispose && !Reconnecting)
                {
                    Interlocked.Exchange(ref SentCount, 0);
                    await Task.Delay(_throttlingPeriod, TokenSource.Token);
                }

                ResetThrottlerRunning = false;
                return Task.CompletedTask;
            });
        }

        public void StartWhisperThrottlingWindowReset()
        {
            ResetWhisperThrottler = Task.Run(async () =>
            {
                ResetWhisperThrottlerRunning = true;
                while (!ShouldDispose && !Reconnecting)
                {
                    Interlocked.Exchange(ref WhispersSent, 0);
                    await Task.Delay(_whisperThrottlingPeriod, TokenSource.Token);
                }

                ResetWhisperThrottlerRunning = false;
                return Task.CompletedTask;
            });
        }

        public void IncrementSentCount()
        {
            Interlocked.Increment(ref SentCount);
        }

        public void IncrementWhisperCount()
        {
            Interlocked.Increment(ref WhispersSent);
        }

        public void AddSendQueue(Tuple<DateTime, string> item)
        {
            SendQueue2.Enqueue(item);
            sendLocker.Release();
        }

        public void AddWhisperQueue(Tuple<DateTime, string> item)
        {
            WhisperQueue2.Enqueue(item);
            whisperLocker.Release();
        }

        public Task StartSenderTask()
        {
            StartThrottlingWindowReset();

            return Task.Run(async () =>
            {
                try
                {
                    while (!ShouldDispose)
                    {
                        await Task.Delay(_client.Options.SendDelay);

                        if (SentCount == _client.Options.MessagesAllowedInPeriod)
                        {
                            _client.MessageThrottled(new OnMessageThrottledEventArgs
                            {
                                Message =
                                    "Message Throttle Occured. Too Many Messages within the period specified in WebsocketClientOptions.",
                                AllowedInPeriod = _client.Options.MessagesAllowedInPeriod,
                                Period = _client.Options.ThrottlingPeriod,
                                SentMessageCount = Interlocked.CompareExchange(ref SentCount, 0, 0)
                            });

                            continue;
                        }

                        if (!_client.IsConnected || ShouldDispose) continue;

                        await sendLocker.WaitAsync(TokenSource.Token);

                        if (!SendQueue2.TryDequeue(out var msg))
                        {
                            sendLocker.Release();
                            continue;
                        }
                        else if (SendQueue2.Count > 0)
                        {
                            sendLocker.Release();
                        }

                        if (msg.Item1.Add(_client.Options.SendCacheItemTimeout) < DateTime.UtcNow) continue;

                        try
                        {
                            switch (_client)
                            {
                                case WebSocketClient ws:
                                    await ws.SendAsync(Encoding.UTF8.GetBytes(msg.Item2));
                                    break;
                                case TcpClient tcp:
                                    await tcp.SendAsync(msg.Item2);
                                    break;
                            }

                            IncrementSentCount();
                        }
                        catch (Exception ex)
                        {
                            _client.SendFailed(new OnSendFailedEventArgs { Data = msg.Item2, Exception = ex });
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _client.SendFailed(new OnSendFailedEventArgs { Data = "", Exception = ex });
                    _client.Error(new OnErrorEventArgs { Exception = ex });
                }
            });
        }

        public Task StartWhisperSenderTask()
        {
            StartWhisperThrottlingWindowReset();

            return Task.Run(async () =>
            {
                try
                {
                    while (!ShouldDispose)
                    {
                        await Task.Delay(_client.Options.SendDelay);

                        if (WhispersSent == _client.Options.WhispersAllowedInPeriod)
                        {
                            _client.WhisperThrottled(new OnWhisperThrottledEventArgs()
                            {
                                Message =
                                    "Whisper Throttle Occured. Too Many Whispers within the period specified in ClientOptions.",
                                AllowedInPeriod = _client.Options.WhispersAllowedInPeriod,
                                Period = _client.Options.WhisperThrottlingPeriod,
                                SentWhisperCount = Interlocked.CompareExchange(ref WhispersSent, 0, 0)
                            });

                            continue;
                        }

                        if (!_client.IsConnected || ShouldDispose) continue;

                        await whisperLocker.WaitAsync(TokenSource.Token);

                        if (!WhisperQueue2.TryDequeue(out var msg))
                        {
                            whisperLocker.Release();
                            continue;
                        }
                        else if (SendQueue2.Count > 0)
                        {
                            whisperLocker.Release();
                        }

                        if (msg.Item1.Add(_client.Options.SendCacheItemTimeout) < DateTime.UtcNow) continue;

                        try
                        {
                            switch (_client)
                            {
                                case WebSocketClient ws:
                                    await ws.SendAsync(Encoding.UTF8.GetBytes(msg.Item2));
                                    break;
                                case TcpClient tcp:
                                    await tcp.SendAsync(msg.Item2);
                                    break;
                            }

                            IncrementWhisperCount();
                        }
                        catch (Exception ex)
                        {
                            _client.SendFailed(new OnSendFailedEventArgs { Data = msg.Item2, Exception = ex });
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _client.SendFailed(new OnSendFailedEventArgs { Data = "", Exception = ex });
                    _client.Error(new OnErrorEventArgs { Exception = ex });
                }
            });
        }
    }
}