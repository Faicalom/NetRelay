using System;
using System.Text;
using System.Text.Json;
using NetRelay.Utils;

namespace NetRelay.Protocol
{
    /// <summary>
    /// Serializes and deserializes protocol messages to/from JSON.
    /// </summary>
    public class MessageSerializer
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "MessageSerializer";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>
        /// Serialize protocol message to JSON bytes.
        /// </summary>
        public byte[] Serialize(ProtocolMessage message)
        {
            try
            {
                var json = JsonSerializer.Serialize(message, JsonOptions);
                return Encoding.UTF8.GetBytes(json);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Failed to serialize message: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Deserialize JSON bytes to protocol message.
        /// </summary>
        public ProtocolMessage? Deserialize(byte[] data)
        {
            try
            {
                var json = Encoding.UTF8.GetString(data);
                return JsonSerializer.Deserialize<ProtocolMessage>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Failed to deserialize message: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Serialize handshake message.
        /// </summary>
        public ProtocolMessage SerializeHandshake(HandshakeMessage msg, string? sessionId = null)
        {
            return new ProtocolMessage
            {
                Type = MessageTypes.Handshake,
                SessionId = sessionId,
                Payload = new()
                {
                    { "deviceName", msg.DeviceName },
                    { "deviceId", msg.DeviceId },
                    { "osVersion", msg.OsVersion },
                    { "appVersion", msg.AppVersion },
                    { "supportedTransports", string.Join(",", msg.SupportedTransports) }
                }
            };
        }

        /// <summary>
        /// Serialize pairing request.
        /// </summary>
        public ProtocolMessage SerializePairingRequest(PairingRequestMessage msg, string? sessionId = null)
        {
            return new ProtocolMessage
            {
                Type = MessageTypes.PairingRequest,
                SessionId = sessionId,
                Payload = new()
                {
                    { "deviceId", msg.DeviceId },
                    { "pairingToken", msg.PairingToken },
                    { "sessionTimeout", msg.SessionTimeout.ToString() }
                }
            };
        }

        /// <summary>
        /// Serialize heartbeat message.
        /// </summary>
        public ProtocolMessage SerializeHeartbeat(HeartbeatMessage msg, string? sessionId = null)
        {
            return new ProtocolMessage
            {
                Type = MessageTypes.Heartbeat,
                SessionId = sessionId,
                Payload = new()
                {
                    { "timestamp", msg.Timestamp.ToString() },
                    { "deviceLoad", msg.DeviceLoad.ToString("F2") }
                }
            };
        }

        /// <summary>
        /// Serialize error message.
        /// </summary>
        public ProtocolMessage SerializeError(ErrorMessage msg, string? sessionId = null)
        {
            return new ProtocolMessage
            {
                Type = MessageTypes.Error,
                SessionId = sessionId,
                Payload = new()
                {
                    { "errorCode", msg.ErrorCode.ToString() },
                    { "errorMessage", msg.ErrorMessageText },
                    { "details", msg.Details ?? "" }
                }
            };
        }

        /// <summary>
        /// Serialize disconnect message.
        /// </summary>
        public ProtocolMessage SerializeDisconnect(DisconnectMessage msg, string? sessionId = null)
        {
            return new ProtocolMessage
            {
                Type = MessageTypes.Disconnect,
                SessionId = sessionId,
                Payload = new()
                {
                    { "reason", msg.Reason },
                    { "willReconnect", msg.WillReconnect.ToString() }
                }
            };
        }
    }
}
