using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using NetRelay.Models;

namespace NetRelay.Services
{
    public sealed class AppSettingsService
    {
        private const string StartupValueName = "NetRelay";
        private static readonly Lazy<AppSettingsService> LazyInstance = new(() => new AppSettingsService());
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly object _gate = new();

        public static AppSettingsService Instance => LazyInstance.Value;
        public ApplicationSettings Current { get; private set; }
        public event EventHandler? Changed;

        private AppSettingsService()
        {
            Current = Load();
        }

        public ApplicationSettings Update(Action<ApplicationSettings> apply)
        {
            lock (_gate)
            {
                var next = Clone(Current);
                apply(next);
                Sanitize(next);
                ApplyStartupRegistration(next.StartWithWindows);
                SaveAtomic(next);
                Current = next;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return Clone(Current);
        }

        public void ClearUiState()
        {
            Update(settings => settings.LastPage = "dashboard");
        }

        private ApplicationSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var value = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(SettingsPath));
                    if (value != null)
                    {
                        Sanitize(value);
                        return value;
                    }
                }
            }
            catch
            {
                // Corrupt settings must never prevent startup.
            }
            return new ApplicationSettings();
        }

        private static void Sanitize(ApplicationSettings value)
        {
            value.Language = value.Language is "en" or "ar" or "fr" or "ru" ? value.Language : "en";
            value.Theme = value.Theme is "midnight" or "oled" or "light" ? value.Theme : "midnight";
            value.PreferredTransport = value.PreferredTransport is "none" or "usb" or "wifiDirect" ? value.PreferredTransport : "none";
            value.LastPage = value.LastPage is "dashboard" or "connection" or "statistics" or "logs" or "settings" or "about" ? value.LastPage : "dashboard";
            value.LoggingLevel = value.LoggingLevel is "normal" or "detailed" ? value.LoggingLevel : "normal";
            value.MaxUiLogs = value.MaxUiLogs is 200 or 500 or 1000 ? value.MaxUiLogs : 500;
        }

        private static ApplicationSettings Clone(ApplicationSettings value) => new()
        {
            Language = value.Language,
            Theme = value.Theme,
            StartWithWindows = value.StartWithWindows,
            StartMinimized = value.StartMinimized,
            CloseToTray = value.CloseToTray,
            AutoReconnect = value.AutoReconnect,
            PreferredTransport = value.PreferredTransport,
            RestoreLastPage = value.RestoreLastPage,
            LastPage = value.LastPage,
            LoggingLevel = value.LoggingLevel,
            MaxUiLogs = value.MaxUiLogs
        };

        private static void SaveAtomic(ApplicationSettings value)
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporaryPath, SettingsPath, true);
        }

        private static void ApplyStartupRegistration(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (enabled)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
                key.SetValue(StartupValueName, $"\"{executable}\" --autostart", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }
        }

        public static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRelay");
        public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    }
}
