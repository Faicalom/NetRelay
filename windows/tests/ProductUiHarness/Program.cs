using NetRelay.Services;
using Microsoft.Win32;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var center = NotificationCenterService.Instance;
if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
{
    const string startupName = "NetRelay";
    var path = AppSettingsService.SettingsPath;
    var existed = File.Exists(path);
    var original = existed ? File.ReadAllBytes(path) : null;
    using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)!;
    var previousStartup = runKey.GetValue(startupName);
    var previousKind = previousStartup == null ? RegistryValueKind.String : runKey.GetValueKind(startupName);
    try
    {
        var settings = AppSettingsService.Instance;
        settings.Update(value =>
        {
            value.Language = "fr";
            value.Theme = "oled";
            value.StartWithWindows = true;
            value.StartMinimized = true;
            value.CloseToTray = true;
            value.AutoReconnect = false;
            value.PreferredTransport = "wifiDirect";
            value.RestoreLastPage = true;
            value.LastPage = "statistics";
            value.LoggingLevel = "detailed";
            value.MaxUiLogs = 1000;
        });
        Require(File.Exists(path) && !File.Exists(path + ".tmp"), "Atomic settings file was not committed.");
        Require(settings.Current.Language == "fr" && settings.Current.Theme == "oled", "Language/theme update failed.");
        Require(settings.Current.PreferredTransport == "wifiDirect" && settings.Current.LoggingLevel == "detailed", "Advanced settings update failed.");
        Require((runKey.GetValue(startupName)?.ToString() ?? string.Empty).Contains("--autostart"), "Startup entry was not added.");
        settings.Update(value => value.StartWithWindows = false);
        Require(runKey.GetValue(startupName) == null, "Startup entry was not removed.");
        Console.WriteLine("PRODUCT_UI_SETTINGS_HARNESS=PASS");
        Console.WriteLine("ATOMIC_PERSISTENCE=PASS");
        Console.WriteLine("STARTUP_ADD_REMOVE=PASS");
        Console.WriteLine("SAFE_ADVANCED_SETTINGS=PASS");
    }
    finally
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (existed) File.WriteAllBytes(path, original!); else File.Delete(path);
        if (previousStartup == null) runKey.DeleteValue(startupName, false); else runKey.SetValue(startupName, previousStartup, previousKind);
    }
    return;
}
if (args.Contains("--clear", StringComparer.OrdinalIgnoreCase))
{
    center.Clear();
    Console.WriteLine("PRODUCT_UI_NOTIFICATION_STATE=CLEARED");
    return;
}
center.Clear();
Require(center.Snapshot().Count == 0, "Empty center must have no badge.");
center.Add("success", "connection", "notificationConnectedTitle", "notificationConnectedMessage");
Require(center.Snapshot().Count == 1 && !center.Snapshot()[0].IsRead, "Synthetic event must create one unread item.");
center.Add("success", "connection", "notificationConnectedTitle", "notificationConnectedMessage");
Require(center.Snapshot().Count == 1, "Duplicate transition must be suppressed.");
center.MarkRead(center.Snapshot()[0].Id);
Require(center.Snapshot().Count(item => !item.IsRead) == 0, "Mark read must clear badge.");
for (var i = 0; i < 120; i++) center.Add("info", "test", $"title-{i}", $"message-{i}");
Require(center.Snapshot().Count == 100, "History must remain bounded to 100 items.");
center.MarkAllRead();
Require(center.Snapshot().All(item => item.IsRead), "Mark all read failed.");
Console.WriteLine("PRODUCT_UI_NOTIFICATION_HARNESS=PASS");
Console.WriteLine("BADGE_EMPTY=PASS");
Console.WriteLine("SYNTHETIC_EVENT=PASS");
Console.WriteLine("DEDUPLICATION=PASS");
Console.WriteLine("MARK_READ=PASS");
Console.WriteLine("HISTORY_LIMIT_100=PASS");
