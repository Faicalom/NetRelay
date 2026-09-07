using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Transport
{
    /// <summary>
    /// Manages available transport implementations.
    /// Routes connection requests to appropriate transport.
    /// CRITICAL: Each transport must be registered separately.
    /// </summary>
    public class TransportManager
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "TransportManager";

        private readonly Dictionary<string, ITransport> _transports = new();
        private ITransport? _activeTransport;
        private readonly Subject<ITransport?> _activeTransportSubject = new();

        public IObservable<ITransport?> ActiveTransportChanged => _activeTransportSubject.AsObservable();

        /// <summary>
        /// Register a transport implementation.
        /// </summary>
        public void RegisterTransport(string transportType, ITransport transport)
        {
            _transports[transportType] = transport;
            Logger.Debug(Tag, $"Transport registered: {transportType}");
        }

        /// <summary>
        /// Get specific transport by type.
        /// </summary>
        public ITransport? GetTransport(string transportType)
        {
            _transports.TryGetValue(transportType, out var transport);
            return transport;
        }

        /// <summary>
        /// Get all registered transport types.
        /// </summary>
        public List<string> GetAvailableTransports()
        {
            return new List<string>(_transports.Keys);
        }

        /// <summary>
        /// Set active transport for connections.
        /// </summary>
        public bool SetActiveTransport(string transportType)
        {
            if (_transports.TryGetValue(transportType, out var transport))
            {
                _activeTransport = transport;
                _activeTransportSubject.OnNext(transport);
                Logger.Info(Tag, $"Active transport set to: {transportType}");
                return true;
            }

            Logger.Error(Tag, $"Transport not found: {transportType}");
            return false;
        }

        /// <summary>
        /// Connect using active transport.
        /// </summary>
        public async Task<bool> ConnectAsync(string target)
        {
            if (_activeTransport != null)
            {
                Logger.Info(Tag, $"Connecting via {_activeTransport.GetTransportType()} to {target}");
                return await _activeTransport.ConnectAsync(target);
            }

            Logger.Error(Tag, "No active transport selected");
            return false;
        }

        /// <summary>
        /// Disconnect from active transport.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_activeTransport != null)
            {
                Logger.Info(Tag, $"Disconnecting from {_activeTransport.GetTransportType()}");
                await _activeTransport.DisconnectAsync();
            }
        }

        /// <summary>
        /// Send data through active transport.
        /// </summary>
        public async Task<bool> SendAsync(byte[] data)
        {
            if (_activeTransport != null)
            {
                return await _activeTransport.SendAsync(data);
            }

            Logger.Error(Tag, "No active transport for sending");
            return false;
        }

        /// <summary>
        /// Observe incoming data from active transport.
        /// </summary>
        public IObservable<byte[]>? ObserveIncoming()
        {
            return _activeTransport?.ObserveIncoming();
        }

        /// <summary>
        /// Observe state changes from active transport.
        /// </summary>
        public IObservable<ITransport.TransportState>? ObserveState()
        {
            return _activeTransport?.ObserveState();
        }

        /// <summary>
        /// Get current connection state.
        /// </summary>
        public ITransport.TransportState? GetState()
        {
            return _activeTransport?.GetState();
        }

        /// <summary>
        /// Check if currently connected.
        /// </summary>
        public bool IsConnected()
        {
            return _activeTransport?.IsConnected() ?? false;
        }

        /// <summary>
        /// Cleanup all transports.
        /// </summary>
        public async Task CleanupAsync()
        {
            Logger.Info(Tag, "Cleaning up all transports");
            foreach (var transport in _transports.Values)
            {
                await transport.CleanupAsync();
                transport.Dispose();
            }
            _transports.Clear();
            _activeTransport = null;
            _activeTransportSubject.OnNext(null);
        }
    }
}
