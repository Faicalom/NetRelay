using System;
using System.Windows;
using NetRelay.UI;
using NetRelay.Utils;
using NetRelay.Services;

namespace NetRelay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var settings = AppSettingsService.Instance.Current;
            Logger.Instance.SetDetailedLogging(settings.LoggingLevel == "detailed");

            Window window = new ChromiumWindow();

            MainWindow = window;
            window.Show();
            if (settings.StartMinimized && Array.Exists(e.Args, value =>
                    string.Equals(value, "--autostart", StringComparison.OrdinalIgnoreCase)))
            {
                window.Hide();
            }
        }
    }
}
