using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Protocol
{
    /// <summary>
    /// Handles incoming protocol messages and coordinates responses.
    /// </summary>
    public class ProtocolHandler
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "ProtocolHandler";

        private readonly MessageSerializer _serializer;
        private readonly Subject<ProtocolMessage> _incomingMessages = new();
        private readonly Subject<ProtocolMessage> _outgoingMessages = new();

        public IObservable<ProtocolMessage> IncomingMessages => _incomingMessages.AsObservable();
        public IObservable<ProtocolMessage> OutgoingMessages => _outgoingMessages.AsObservable();

        public ProtocolHandler(MessageSerializer serializer)
        {
            _serializer = serializer;
        }

        /// <summary>
        /// Process incoming raw data and parse as protocol message.
        /// </summary>
        public async Task ProcessIncomingAsync(byte[] data)
        {
            var message = _serializer.Deserialize(data);
            if (message != null)
            {
                Logger.Debug(Tag, $"Incoming message: {message.Type} from session: {message.SessionId}");
                _incomingMessages.OnNext(message);
            }
            else
            {
                Logger.Warn(Tag, "Failed to deserialize incoming message");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Send outgoing protocol message.
        /// </summary>
        public byte[] SendMessage(ProtocolMessage message)
        {
            Logger.Debug(Tag, $"Sending message: {message.Type} to session: {message.SessionId}");
            _outgoingMessages.OnNext(message);
            return _serializer.Serialize(message);
        }

        /// <summary>
        /// Send handshake message.
        /// </summary>
        public byte[] SendHandshake(
            string deviceName,
            string deviceId,
            string osVersion,
            string appVersion,
            string[] supportedTransports,
            string? sessionId = null)
        {
            var handshake = new HandshakeMessage
            {
                DeviceName = deviceName,
                DeviceId = deviceId,
                OsVersion = osVersion,
                AppVersion = appVersion,
                SupportedTransports = new(supportedTransports)
            };
            var message = _serializer.SerializeHandshake(handshake, sessionId);
            // Extra fields are intentionally additive so older Android builds can ignore them.
            // v2 uses a PdaNet-like split: control channel + independent socket gateway.
            message.Payload["capabilities"] =
                "gateway.ael2.tcpudp.v1,auto-reconnect.v1";
            message.Payload["preferredGatewayPort"] = "8991";
            message.Payload["localSocksPort"] = "8990";
            return SendMessage(message);
        }

        /// <summary>
        /// Send pairing request message.
        /// </summary>
        public byte[] SendPairingRequest(
            string deviceId,
            string pairingToken,
            long sessionTimeout,
            string? sessionId = null)
        {
            var pairing = new PairingRequestMessage
            {
                DeviceId = deviceId,
                PairingToken = pairingToken,
                SessionTimeout = sessionTimeout
            };
            var message = _serializer.SerializePairingRequest(pairing, sessionId);
            return SendMessage(message);
        }

        /// <summary>
        /// Send heartbeat message.
        /// </summary>
        public byte[] SendHeartbeat(float deviceLoad = 0f, string? sessionId = null)
        {
            var heartbeat = new HeartbeatMessage { DeviceLoad = deviceLoad };
            var message = _serializer.SerializeHeartbeat(heartbeat, sessionId);
            return SendMessage(message);
        }

        /// <summary>
        /// Send error message.
        /// </summary>
        public byte[] SendError(
            int errorCode,
            string errorMessage,
            string? details = null,
            string? sessionId = null)
        {
            var error = new ErrorMessage
            {
                ErrorCode = errorCode,
                ErrorMessageText = errorMessage,
                Details = details
            };
            var message = _serializer.SerializeError(error, sessionId);
            return SendMessage(message);
        }

        /// <summary>
        /// Send disconnect message.
        /// </summary>
        public byte[] SendDisconnect(
            string reason = "User initiated",
            bool willReconnect = false,
            string? sessionId = null)
        {
            var disconnect = new DisconnectMessage
            {
                Reason = reason,
                WillReconnect = willReconnect
            };
            var message = _serializer.SerializeDisconnect(disconnect, sessionId);
            return SendMessage(message);
        }
    }
}
