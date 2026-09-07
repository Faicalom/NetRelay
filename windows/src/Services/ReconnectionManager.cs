using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using NetRelay.Models;
using NetRelay.Utils;

namespace NetRelay.Services
{
    /// <summary>
    /// Manages automatic reconnection with exponential backoff.
    /// </summary>
    public class ReconnectionManager
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "ReconnectionManager";

        private readonly Transport.TransportManager _transportManager;
        private readonly SessionManager _sessionManager;

        private bool _isReconnecting;
        private int _reconnectAttempt;
        private const int MaxReconnectAttempts = 5;
        private const long InitialBackoffMs = 1000;

        private readonly Subject<bool> _isReconnectingSubject = new();
        private readonly Subject<int> _reconnectAttemptSubject = new();

        public IObservable<bool> IsReconnectingChanged => _isReconnectingSubject.AsObservable();
        public IObservable<int> ReconnectAttemptChanged => _reconnectAttemptSubject.AsObservable();

        public ReconnectionManager(
            Transport.TransportManager transportManager,
            SessionManager sessionManager)
        {
            _transportManager = transportManager;
            _sessionManager = sessionManager;
        }

        /// <summary>
        /// Start reconnection attempts.
        /// </summary>
        public async Task StartReconnectionAsync(string target, string transportType)
        {
            if (_isReconnecting)
            {
                Logger.Debug(Tag, "Already reconnecting");
                return;
            }

            Logger.Info(Tag, "Starting reconnection sequence");
            _isReconnecting = true;
            _reconnectAttempt = 0;
            _isReconnectingSubject.OnNext(true);

            await AttemptReconnectionAsync(target, transportType);
        }

        /// <summary>
        /// Stop reconnection attempts.
        /// </summary>
        public void StopReconnection()
        {
            Logger.Info(Tag, "Stopping reconnection");
            _isReconnecting = false;
            _reconnectAttempt = 0;
            _isReconnectingSubject.OnNext(false);
        }

        private long GetBackoffTime(int attempt)
        {
            // Exponential backoff: 1s, 2s, 4s, 8s, 16s
            return InitialBackoffMs * (long)Math.Pow(2, attempt - 1);
        }

        private async Task AttemptReconnectionAsync(string target, string transportType)
        {
            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                try
                {
                    _reconnectAttempt = attempt;
                    _reconnectAttemptSubject.OnNext(attempt);

                    long backoffTime = GetBackoffTime(attempt);
                    Logger.Info(Tag, $"Reconnection attempt {attempt}/{MaxReconnectAttempts} (backoff: {backoffTime}ms)");

                    await Task.Delay((int)backoffTime);

                    var connected = await _transportManager.ConnectAsync(target);
                    if (connected)
                    {
                        Logger.Info(Tag, $"Reconnection successful on attempt {attempt}");
                        _sessionManager.MarkActive();
                        _isReconnecting = false;
                        _isReconnectingSubject.OnNext(false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(Tag, $"Reconnection attempt {attempt} failed: {ex.Message}");
                }
            }

            Logger.Error(Tag, $"All reconnection attempts failed");
            _sessionManager.MarkError($"Failed to reconnect after {MaxReconnectAttempts} attempts");
            _isReconnecting = false;
            _isReconnectingSubject.OnNext(false);
        }
    }
}
