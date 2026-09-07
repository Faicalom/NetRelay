using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NetRelay.Protocol
{
    /// <summary>
    /// Base message structure for all protocol messages.
    /// </summary>
    public class ProtocolMessage
    {
        [JsonPropertyName("messageId")]
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = MessageTypes.ProtocolVersion;

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("payload")]
        public Dictionary<string, string> Payload { get; set; } = new();
    }

    /// <summary>
    /// Handshake message - Initial connection greeting.
    /// </summary>
    public class HandshakeMessage
    {
        [JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = string.Empty;

        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = string.Empty;

        [JsonPropertyName("osVersion")]
        public string OsVersion { get; set; } = string.Empty;

        [JsonPropertyName("appVersion")]
        public string AppVersion { get; set; } = string.Empty;

        [JsonPropertyName("supportedTransports")]
        public List<string> SupportedTransports { get; set; } = new();
    }

    /// <summary>
    /// Pairing request - Request to establish session.
    /// </summary>
    public class PairingRequestMessage
    {
        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = string.Empty;

        [JsonPropertyName("pairingToken")]
        public string PairingToken { get; set; } = string.Empty;

        [JsonPropertyName("sessionTimeout")]
        public long SessionTimeout { get; set; } = 3600000; // 1 hour
    }

    /// <summary>
    /// Pairing response - Acknowledgment with session token.
    /// </summary>
    public class PairingResponseMessage
    {
        [JsonPropertyName("accepted")]
        public bool Accepted { get; set; }

        [JsonPropertyName("sessionToken")]
        public string? SessionToken { get; set; }

        [JsonPropertyName("reasonIfRejected")]
        public string? ReasonIfRejected { get; set; }
    }

    /// <summary>
    /// Heartbeat message - Keep-alive ping.
    /// </summary>
    public class HeartbeatMessage
    {
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [JsonPropertyName("deviceLoad")]
        public float DeviceLoad { get; set; }
    }

    /// <summary>
    /// Data transfer message - Payload data.
    /// </summary>
    public class DataTransferMessage
    {
        [JsonPropertyName("sequenceNumber")]
        public long SequenceNumber { get; set; }

        [JsonPropertyName("dataSize")]
        public int DataSize { get; set; }

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;

        [JsonPropertyName("isLast")]
        public bool IsLast { get; set; }
    }

    /// <summary>
    /// Status update message - Device status information.
    /// </summary>
    public class StatusUpdateMessage
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public Dictionary<string, string> Details { get; set; } = new();
    }

    /// <summary>
    /// Error message - Error reporting.
    /// </summary>
    public class ErrorMessage
    {
        [JsonPropertyName("errorCode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("errorMessage")]
        public string ErrorMessageText { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public string? Details { get; set; }
    }

    /// <summary>
    /// Disconnect message - Connection termination.
    /// </summary>
    public class DisconnectMessage
    {
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "User initiated";

        [JsonPropertyName("willReconnect")]
        public bool WillReconnect { get; set; }
    }
}
