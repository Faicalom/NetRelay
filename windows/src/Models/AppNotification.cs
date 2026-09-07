using System;
using System.Collections.Generic;

namespace NetRelay.Models
{
    public sealed class AppNotification
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
        public string Severity { get; set; } = "info";
        public string Category { get; set; } = "system";
        public string TitleKey { get; set; } = string.Empty;
        public string MessageKey { get; set; } = string.Empty;
        public Dictionary<string, string> MessageArgs { get; set; } = new();
        public bool IsRead { get; set; }
    }
}
