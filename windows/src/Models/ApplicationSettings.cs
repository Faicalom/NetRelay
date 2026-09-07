namespace NetRelay.Models
{
    public sealed class ApplicationSettings
    {
        public string Language { get; set; } = "en";
        public string Theme { get; set; } = "midnight";
        public bool StartWithWindows { get; set; }
        public bool StartMinimized { get; set; }
        public bool CloseToTray { get; set; }
        public bool AutoReconnect { get; set; } = true;
        public string PreferredTransport { get; set; } = "none";
        public bool RestoreLastPage { get; set; } = true;
        public string LastPage { get; set; } = "dashboard";
        public string LoggingLevel { get; set; } = "normal";
        public int MaxUiLogs { get; set; } = 500;
    }
}
