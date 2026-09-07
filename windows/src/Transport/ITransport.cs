using System;
using System.Threading.Tasks;

namespace NetRelay.Transport
{
    /// <summary>
    /// Base transport interface that all implementations must follow.
    /// Each transport type (Wi-Fi Direct or USB) must implement this interface.
    /// CRITICAL RULE: Transport layer MUST be abstracted.
    /// DO NOT mix transport implementations.
    /// </summary>
    public interface ITransport : IDisposable
    {
        public enum TransportState
        {
            Disconnected,
            Connecting,
            Connected,
            Disconnecting,
            Error
        }

        public class TransportError
        {
            public int Code { get; set; }
            public string Message { get; set; } = string.Empty;
            public Exception? Cause { get; set; }
        }

        /// <summary>
        /// Establish connection with target device.
        /// </summary>
        Task<bool> ConnectAsync(string target);

        /// <summary>
        /// Disconnect from target device.
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Send data to connected device.
        /// </summary>
        Task<bool> SendAsync(byte[] data);

        /// <summary>
        /// Observable for incoming data.
        /// </summary>
        IObservable<byte[]> ObserveIncoming();

        /// <summary>
        /// Observable for state changes.
        /// </summary>
        IObservable<TransportState> ObserveState();

        /// <summary>
        /// Observable for transport errors.
        /// </summary>
        IObservable<TransportError> ObserveErrors();

        /// <summary>
        /// Get current transport state.
        /// </summary>
        TransportState GetState();

        /// <summary>
        /// Check if transport is connected.
        /// </summary>
        bool IsConnected();

        /// <summary>
        /// Get transport type name.
        /// </summary>
        string GetTransportType();

        /// <summary>
        /// Cleanup resources.
        /// </summary>
        Task CleanupAsync();
    }
}
