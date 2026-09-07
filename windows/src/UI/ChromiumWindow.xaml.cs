using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using NetRelay.Services;
using NetRelay.Utils;
using NetRelay.ViewModels;

namespace NetRelay.UI
{
    public partial class ChromiumWindow : Window
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string LogTag = "WebUiHost";
        private readonly ConnectionViewModel _viewModel = new();
        private TrayIconService? _trayIconService;
        private WebUiBridge? _bridge;
        private bool _isClosing;
        private bool _forceExit;

        public ChromiumWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => FitInitialWindowToMonitor();
            DataContext = _viewModel;
            _viewModel.AutoReconnectEnabled = AppSettingsService.Instance.Current.AutoReconnect;
            _trayIconService = new TrayIconService(this, _viewModel, RequestExit);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (IsLoaded && WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(new Action(() => FitInitialWindowToMonitor(false)));
        }

        private void FitInitialWindowToMonitor(bool initial = true)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            // WorkingArea is physical pixels; WPF dimensions are device-independent.
            // Preserve the accepted desktop size when it fits, never exceed the
            // current monitor's work area (including high scaling and taskbar).
            var availableWidth = work.Width / dpi.DpiScaleX;
            var availableHeight = work.Height / dpi.DpiScaleY;
            Width = Math.Min(initial ? 1380 : ActualWidth, Math.Max(1, availableWidth - 32));
            Height = Math.Min(initial ? 860 : ActualHeight, Math.Max(1, availableHeight - 32));
            MinWidth = Math.Min(320, Width);
            MinHeight = Math.Min(300, Height);
            if (initial)
            {
                Left = work.Left / dpi.DpiScaleX + (availableWidth - Width) / 2;
                Top = work.Top / dpi.DpiScaleY + (availableHeight - Height) / 2;
            }
            Logger.Info(LogTag, $"[NETRELAY-DISPLAY] workAreaPx={work.Width}x{work.Height} dpi={dpi.PixelsPerInchX} windowDip={Width}x{Height}");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var distPath = Path.Combine(AppContext.BaseDirectory, "ui", "dist");
                var indexPath = Path.Combine(distPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    throw new FileNotFoundException("Packaged Chromium UI was not found.", indexPath);
                }

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    throw new InvalidOperationException("Local application data directory is unavailable.");
                }

                var userDataFolder = Path.Combine(localAppData, "Net Relay", "WebView2");
                Directory.CreateDirectory(userDataFolder);
                var webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);
                await WebView.EnsureCoreWebView2Async(webViewEnvironment);
                WebView.ZoomFactor = 1.0;
                var core = WebView.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 Core initialization returned no instance.");

                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.NavigationCompleted += async (_, args) =>
                {
                    if (!args.IsSuccess || _isClosing) return;
                    try
                    {
                        var display = await core.ExecuteScriptAsync(
                            "JSON.stringify({width:innerWidth,height:innerHeight,dpr:devicePixelRatio})");
                        Logger.Info(LogTag, $"[NETRELAY-DISPLAY] zoom={WebView.ZoomFactor} viewport={display}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(LogTag, $"[NETRELAY-DISPLAY] Viewport measurement unavailable: {ex.Message}");
                    }
                };
#if DEBUG
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.AreDefaultContextMenusEnabled = true;
#else
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
#endif
                core.SetVirtualHostNameToFolderMapping(
                    "netrelay.local",
                    distPath,
                    CoreWebView2HostResourceAccessKind.Deny);
                core.NavigationStarting += (_, args) =>
                {
                    if (args.Uri == "about:blank" ||
                        args.Uri.StartsWith("https://netrelay.local/", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    args.Cancel = true;
                    Logger.Warn(LogTag, "[NETRELAY-WEBUI] Blocked navigation outside local origin");
                };
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;

                _bridge = new WebUiBridge(core, _viewModel, Dispatcher, () =>
                {
                    if (!_isClosing)
                    {
                        LoadingPanel.Visibility = Visibility.Collapsed;
                        Logger.Info(LogTag, "[NETRELAY-WEBUI] Loading panel hidden after uiReady");
                    }
                });
                core.Navigate("https://netrelay.local/index.html");
                Logger.Info(LogTag, $"[NETRELAY-WEBUI] WebView initialized origin=https://netrelay.local/ userData={userDataFolder}");
            }
            catch (Exception ex)
            {
                Logger.Error(LogTag, $"[NETRELAY-WEBUI] Initialization failed: {ex.Message}", ex);
                if (!_isClosing)
                {
                    _forceExit = true;
                    var guidance = ex is WebView2RuntimeNotFoundException
                        ? "Microsoft Edge WebView2 Runtime is missing. Run the bundled installer or repair Net Relay."
                        : ex is FileNotFoundException
                            ? "Required interface files are missing. Repair or reinstall Net Relay."
                            : "The Chromium interface could not be initialized. Repair Net Relay or WebView2 Runtime. Details are recorded in the application log.";
                    MessageBox.Show(this,
                        "Net Relay could not start its Chromium interface.\n\n" + guidance + "\n\nThe application will close.",
                        "Net Relay — Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                }
            }
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_isClosing)
            {
                return;
            }

            if (!_forceExit && AppSettingsService.Instance.Current.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            e.Cancel = true;
            _isClosing = true;
            IsEnabled = false;
            try
            {
                await _viewModel.ShutdownAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                Logger.Error(LogTag, $"[NETRELAY-WEBUI] Shutdown cleanup failed: {ex.Message}", ex);
            }
            finally
            {
                _bridge?.Dispose();
                _bridge = null;
                _trayIconService?.Dispose();
                _trayIconService = null;
                WebView.Dispose();
                Closing -= Window_Closing;
                Close();
            }
        }

        private void RequestExit()
        {
            _forceExit = true;
            Show();
            Close();
        }
    }
}
