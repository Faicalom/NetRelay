using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using NetRelay.Models;
using NetRelay.Utils;

namespace NetRelay.Services
{
    /// <summary>
    /// Manages connection sessions.
    /// Tracks pairing, session tokens, timeouts, and reconnections.
    /// </summary>
    public class SessionManager
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "SessionManager";

        private ConnectionSession? _currentSession;
        private SessionState _sessionState = SessionState.Closed;
        private readonly Subject<ConnectionSession?> _currentSessionSubject = new();
        private readonly Subject<SessionState> _sessionStateSubject = new();

        private readonly Dictionary<string, Device> _pairedDevices = new();

        public IObservable<ConnectionSession?> CurrentSessionChanged => _currentSessionSubject.AsObservable();
        public IObservable<SessionState> SessionStateChanged => _sessionStateSubject.AsObservable();

        /// <summary>
        /// Initiate new session with device.
        /// </summary>
        public string InitiateSession(Device device)
        {
            var sessionId = Guid.NewGuid().ToString();
            _currentSession = new ConnectionSession
            {
                SessionId = sessionId,
                Device = device,
                SessionToken = GenerateSessionToken(),
                State = SessionState.Initiated
            };

            _currentSessionSubject.OnNext(_currentSession);
            UpdateSessionState(SessionState.Initiated);
            Logger.Info(Tag, $"Session initiated: {sessionId} for device: {device.DeviceName}");

            return sessionId;
        }

        /// <summary>
        /// Move session to handshake phase.
        /// </summary>
        public bool StartHandshake()
        {
            if (_currentSession != null)
            {
                UpdateSessionState(SessionState.Handshaking);
                Logger.Debug(Tag, $"Handshake started for session: {_currentSession.SessionId}");
                return true;
            }

            Logger.Error(Tag, "No active session for handshake");
            return false;
        }

        /// <summary>
        /// Move session to pairing phase.
        /// </summary>
        public bool StartPairing()
        {
            if (_currentSession != null)
            {
                UpdateSessionState(SessionState.Pairing);
                Logger.Debug(Tag, $"Pairing started for session: {_currentSession.SessionId}");
                return true;
            }

            Logger.Error(Tag, "No active session for pairing");
            return false;
        }

        /// <summary>
        /// Mark session as active (fully connected).
        /// </summary>
        public bool MarkActive()
        {
            if (_currentSession?.Device != null)
            {
                var device = _currentSession.Device;
                device.IsPaired = true;
                _pairedDevices[device.DeviceId] = device;
                UpdateSessionState(SessionState.Active);
                Logger.Info(Tag, $"Session marked as ACTIVE: {_currentSession.SessionId}");
                return true;
            }

            Logger.Error(Tag, "No active session to mark");
            return false;
        }

        /// <summary>
        /// Get current session.
        /// </summary>
        public ConnectionSession? GetSession()
        {
            return _currentSession;
        }

        /// <summary>
        /// Get current session state.
        /// </summary>
        public SessionState GetSessionState()
        {
            return _sessionState;
        }

        /// <summary>
        /// Check if session is active.
        /// </summary>
        public bool IsSessionActive()
        {
            return _sessionState == SessionState.Active;
        }

        /// <summary>
        /// Validate session token.
        /// </summary>
        public bool ValidateSessionToken(string token)
        {
            return _currentSession?.SessionToken == token;
        }

        /// <summary>
        /// Get paired devices.
        /// </summary>
        public List<Device> GetPairedDevices()
        {
            return new List<Device>(_pairedDevices.Values);
        }

        /// <summary>
        /// Check if device is paired.
        /// </summary>
        public bool IsDevicePaired(string deviceId)
        {
            return _pairedDevices.ContainsKey(deviceId);
        }

        /// <summary>
        /// Add paired device.
        /// </summary>
        public void AddPairedDevice(Device device)
        {
            device.IsPaired = true;
            _pairedDevices[device.DeviceId] = device;
            Logger.Debug(Tag, $"Device paired: {device.DeviceName}");
        }

        /// <summary>
        /// Remove paired device.
        /// </summary>
        public void RemovePairedDevice(string deviceId)
        {
            _pairedDevices.Remove(deviceId);
            Logger.Debug(Tag, $"Device unpaired: {deviceId}");
        }

        /// <summary>
        /// Handle reconnect scenario.
        /// </summary>
        public void HandleReconnect()
        {
            UpdateSessionState(SessionState.Reconnecting);
            Logger.Info(Tag, "Session reconnecting...");
        }

        /// <summary>
        /// Close session.
        /// </summary>
        public async Task CloseSessionAsync(string reason = "User initiated")
        {
            if (_currentSession != null)
            {
                Logger.Info(Tag, $"Session closed: {_currentSession.SessionId} - Reason: {reason}");
            }
            UpdateSessionState(SessionState.Closed);
            _currentSession = null;
            _currentSessionSubject.OnNext(null);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Mark session as error state.
        /// </summary>
        public void MarkError(string error)
        {
            UpdateSessionState(SessionState.Error);
            Logger.Error(Tag, $"Session error: {error}");
        }

        private void UpdateSessionState(SessionState state)
        {
            _sessionState = state;
            _sessionStateSubject.OnNext(state);
        }

        private static string GenerateSessionToken()
        {
            return Guid.NewGuid().ToString().Replace("-", "");
        }
    }
}
