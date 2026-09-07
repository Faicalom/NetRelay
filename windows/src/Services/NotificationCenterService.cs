using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NetRelay.Models;

namespace NetRelay.Services
{
    public sealed class NotificationCenterService
    {
        private const int MaxNotifications = 100;
        private static readonly Lazy<NotificationCenterService> LazyInstance = new(() => new NotificationCenterService());
        private readonly object _gate = new();
        private List<AppNotification> _items;

        public static NotificationCenterService Instance => LazyInstance.Value;
        public event EventHandler? Changed;

        private NotificationCenterService()
        {
            _items = Load();
        }

        public IReadOnlyList<AppNotification> Snapshot()
        {
            lock (_gate) return _items.Select(Clone).ToArray();
        }

        public void Add(string severity, string category, string titleKey, string messageKey, Dictionary<string, string>? args = null)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.Now;
                var duplicate = _items.FirstOrDefault(item =>
                    item.Category == category && item.TitleKey == titleKey && item.MessageKey == messageKey &&
                    now - item.Timestamp < TimeSpan.FromSeconds(8));
                if (duplicate != null) return;

                _items.Insert(0, new AppNotification
                {
                    Timestamp = now,
                    Severity = severity,
                    Category = category,
                    TitleKey = titleKey,
                    MessageKey = messageKey,
                    MessageArgs = args ?? new Dictionary<string, string>()
                });
                if (_items.Count > MaxNotifications) _items.RemoveRange(MaxNotifications, _items.Count - MaxNotifications);
                SaveAtomic();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void MarkRead(string id)
        {
            lock (_gate)
            {
                var item = _items.FirstOrDefault(value => value.Id == id);
                if (item == null || item.IsRead) return;
                item.IsRead = true;
                SaveAtomic();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void MarkAllRead()
        {
            lock (_gate)
            {
                foreach (var item in _items) item.IsRead = true;
                SaveAtomic();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            lock (_gate) { _items.Clear(); SaveAtomic(); }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private List<AppNotification> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<AppNotification>();
                return (JsonSerializer.Deserialize<List<AppNotification>>(File.ReadAllText(FilePath)) ?? new())
                    .OrderByDescending(item => item.Timestamp).Take(MaxNotifications).ToList();
            }
            catch { return new List<AppNotification>(); }
        }

        private void SaveAtomic()
        {
            Directory.CreateDirectory(AppSettingsService.SettingsDirectory);
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_items));
            File.Move(temporaryPath, FilePath, true);
        }

        private static AppNotification Clone(AppNotification item) => new()
        {
            Id = item.Id,
            Timestamp = item.Timestamp,
            Severity = item.Severity,
            Category = item.Category,
            TitleKey = item.TitleKey,
            MessageKey = item.MessageKey,
            MessageArgs = new Dictionary<string, string>(item.MessageArgs),
            IsRead = item.IsRead
        };

        private static string FilePath => Path.Combine(AppSettingsService.SettingsDirectory, "notifications.json");
    }
}
