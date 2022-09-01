using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TwitchLib.Communication.Events;

namespace TwitchLib.Communication.Interfaces
{
    public interface IThrottleClient
    {
        /// <summary>
        /// The current state of the connection.
        /// </summary>
        bool IsConnected { get; }
        
        /// <summary>
        /// Client Configuration Options
        /// </summary>
        IClientOptions Options {get;}

        void MessageThrottled(OnMessageThrottledEventArgs eventArgs);
        void SendFailed(OnSendFailedEventArgs eventArgs);
        void Error(OnErrorEventArgs eventArgs);
        void WhisperThrottled(OnWhisperThrottledEventArgs eventArgs);
    }
}