namespace NetRelay.Models
{
    /// <summary>
    /// UI-friendly transport option with display metadata.
    /// </summary>
    public class TransportOption
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsImplemented { get; set; }
        public string Hint { get; set; } = string.Empty;
    }
}
