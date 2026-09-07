using System;

namespace NetRelay.Models
{
    /// <summary>
    /// Represents a connected device.
    /// </summary>
    public class Device
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public long LastSeen { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public bool IsPaired { get; set; }
        public string TransportType { get; set; } = string.Empty;
        public string ProxyEndpoint { get; set; } = string.Empty;
        public string GatewayEndpoint { get; set; } = string.Empty;
    }

    /// <summary>
    /// Connection session state.
    /// </summary>
    public class ConnectionSession
    {
        public string SessionId { get; set; } = string.Empty;
        public Device? Device { get; set; }
        public string SessionToken { get; set; } = string.Empty;
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long SessionTimeout { get; set; } = 3600000;
        public SessionState State { get; set; } = SessionState.Initiated;
    }

    /// <summary>
    /// Session states.
    /// </summary>
    public enum SessionState
    {
        Initiated,
        Handshaking,
        Pairing,
        Active,
        Reconnecting,
        Disconnecting,
        Closed,
        Error
    }

    /// <summary>
    /// Connection statistics.
    /// </summary>
    public class ConnectionStats
    {
        public long DataBytesSent { get; set; }
        public long DataBytesReceived { get; set; }
        public long MessagesReceived { get; set; }
        public long MessagesSent { get; set; }
        public long PacketsLost { get; set; }
        public long AverageLatency { get; set; }
        public long ConnectionDuration { get; set; }
    }
}
