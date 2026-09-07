namespace NetRelay.Protocol
{
    /// <summary>
    /// Protocol message type definitions.
    /// Each message type represents a specific communication intent.
    /// </summary>
    public static class MessageTypes
    {
        // Handshake and pairing
        public const string Handshake = "HANDSHAKE";
        public const string HandshakeResponse = "HANDSHAKE_RESPONSE";
        public const string PairingRequest = "PAIRING_REQUEST";
        public const string PairingResponse = "PAIRING_RESPONSE";
        public const string PairingConfirmed = "PAIRING_CONFIRMED";

        // Keep-alive
        public const string Heartbeat = "HEARTBEAT";
        public const string HeartbeatAck = "HEARTBEAT_ACK";

        // Data transfer
        public const string DataTransfer = "DATA_TRANSFER";
        public const string DataAck = "DATA_ACK";

        // Status
        public const string StatusUpdate = "STATUS_UPDATE";
        public const string StatusQuery = "STATUS_QUERY";

        // Error handling
        public const string Error = "ERROR";
        public const string ErrorAck = "ERROR_ACK";

        // Connection management
        public const string Disconnect = "DISCONNECT";
        public const string DisconnectAck = "DISCONNECT_ACK";
        public const string ReconnectRequest = "RECONNECT_REQUEST";

        // Protocol
        public const string ProtocolVersion = "1.0";
    }

    /// <summary>
    /// Protocol error codes.
    /// </summary>
    public static class ErrorCodes
    {
        public const int UnknownError = 0;
        public const int InvalidMessage = 1;
        public const int AuthenticationFailed = 2;
        public const int InvalidSession = 3;
        public const int TransportError = 4;
        public const int Timeout = 5;
        public const int ProtocolVersionMismatch = 6;
        public const int DeviceIncompatible = 7;
    }
}
