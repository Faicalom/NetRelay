using System;

namespace NetRelay.ViewModels
{
    /// <summary>
    /// Compatibility alias for older code. Automatic heartbeat and recovery now live
    /// in the main ConnectionViewModel, so creating a second TransportManager here
    /// would be incorrect and could never reconnect registered transports.
    /// </summary>
    [Obsolete("Use ConnectionViewModel. Recovery is integrated in the primary view model.")]
    public class ConnectionViewModelV2 : ConnectionViewModel
    {
    }

    public enum ConnectionHealth
    {
        Unknown,
        Excellent,
        Good,
        Fair,
        Poor
    }
}
