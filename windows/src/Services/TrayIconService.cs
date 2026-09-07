using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using NetRelay.ViewModels;
using WinApplication = System.Windows.Application;
using WinFormsApplication = System.Windows.Forms.Application;

namespace NetRelay.Services
{
    public sealed class TrayIconService : IDisposable
    {
        private readonly Window _window;
        private readonly ConnectionViewModel _viewModel;
        private readonly NotifyIcon _notifyIcon;
        private readonly Action _exitAction;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _disconnectItem;
        private readonly ToolStripMenuItem _exitItem;

        public TrayIconService(Window window, ConnectionViewModel viewModel, Action? exitAction = null)
        {
            _window = window;
            _viewModel = viewModel;
            _exitAction = exitAction ?? (() => WinApplication.Current.Shutdown());
            _openItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowWindow());
            _disconnectItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await _viewModel.DisconnectAsync());
            _exitItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ExitFromTray());
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { _openItem, _disconnectItem, new ToolStripSeparator(), _exitItem });
            _notifyIcon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SystemIcons.Application,
                Text = "Net Relay",
                Visible = true,
                ContextMenuStrip = menu
            };

            _notifyIcon.DoubleClick += (_, _) => ShowWindow();
            AppSettingsService.Instance.Changed += Settings_Changed;
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ConnectionViewModel.IsConnected)
                    or nameof(ConnectionViewModel.IsAdapterModeRunning)
                    or nameof(ConnectionViewModel.AdapterModeStatus)
                    or nameof(ConnectionViewModel.StatusMessage))
                {
                    UpdateStatus();
                }
            };

            UpdateStatus();
        }

        public void Dispose()
        {
            AppSettingsService.Instance.Changed -= Settings_Changed;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        private void ShowWindow()
        {
            _window.Show();
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        private void ExitFromTray()
        {
            _notifyIcon.Visible = false;
            _exitAction();
        }

        private void UpdateStatus()
        {
            var language = AppSettingsService.Instance.Current.Language;
            var labels = GetLabels(language);
            _openItem.Text = labels.Open;
            _disconnectItem.Text = labels.Disconnect;
            _exitItem.Text = labels.Exit;
            _disconnectItem.Enabled = _viewModel.IsConnected;
            var status = _viewModel.IsAdapterModeRunning ? labels.Connected : _viewModel.IsConnected ? labels.Connected : labels.Disconnected;

            _notifyIcon.Text = TrimTooltip($"Net Relay - {status}");
            if (_viewModel.IsAdapterModeRunning)
            {
                WinFormsApplication.DoEvents();
            }
        }

        private void Settings_Changed(object? sender, EventArgs e) => UpdateStatus();

        private static string TrimTooltip(string text)
        {
            return text.Length <= 63 ? text : text[..63];
        }

        private static (string Open, string Disconnect, string Exit, string Connected, string Disconnected) GetLabels(string language) => language switch
        {
            "ar" => ("فتح Net Relay", "قطع الاتصال", "خروج", "متصل", "غير متصل"),
            "fr" => ("Ouvrir Net Relay", "Déconnecter", "Quitter", "Connecté", "Non connecté"),
            "ru" => ("Открыть Net Relay", "Отключиться", "Выход", "Подключено", "Не подключено"),
            _ => ("Open Net Relay", "Disconnect", "Exit", "Connected", "Not connected")
        };
    }
}
