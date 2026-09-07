using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NetRelay.Models;
using NetRelay.Services;
using NetRelay.Utils;
using NetRelay.ViewModels;

namespace NetRelay.UI
{
    internal sealed class WebUiBridge : IDisposable
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "WebUiBridge";
        private static readonly Regex SecretPattern = new(
            @"(?i)(password|passphrase|token)\s*[:=]\s*[^\s,;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly CoreWebView2 _webView;
        private readonly ConnectionViewModel _viewModel;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly Action? _onUiReady;
        private string _lastConnectionJson = string.Empty;
        private string _lastMetricsJson = string.Empty;
        private string _lastLogsJson = string.Empty;
        private string _lastNotificationsJson = string.Empty;
        private string _lastSettingsJson = string.Empty;
        private DateTimeOffset? _connectedSince;
        private bool _disposed;
        private bool _uiReady;
        private bool _uiReadyNotified;
        private bool _connectionDirty = true;
        private bool _usbDiscoveryAttempted;
        private bool _notificationsDirty = true;
        private bool _settingsDirty = true;
        private bool _transitionInitialized;
        private bool _previousConnected;
        private bool _previousInternet;
        private string _previousUiState = string.Empty;
        private string _previousAdbStatus = string.Empty;
        private readonly AppSettingsService _settings = AppSettingsService.Instance;
        private readonly NotificationCenterService _notifications = NotificationCenterService.Instance;

        public WebUiBridge(
            CoreWebView2 webView,
            ConnectionViewModel viewModel,
            Dispatcher dispatcher,
            Action? onUiReady = null)
        {
            _webView = webView;
            _viewModel = viewModel;
            _dispatcher = dispatcher;
            _onUiReady = onUiReady;
            _webView.WebMessageReceived += WebView_WebMessageReceived;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _notifications.Changed += Notifications_Changed;
            _settings.Changed += Settings_Changed;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, Timer_Tick, dispatcher);
            _timer.Start();
            Logger.Info(Tag, "[NETRELAY-WEBUI] Bridge created");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _notifications.Changed -= Notifications_Changed;
            _settings.Changed -= Settings_Changed;
            _webView.WebMessageReceived -= WebView_WebMessageReceived;
        }

        private void Notifications_Changed(object? sender, EventArgs e) => _notificationsDirty = true;

        private void Settings_Changed(object? sender, EventArgs e)
        {
            _connectionDirty = true;
            _settingsDirty = true;
            _viewModel.AutoReconnectEnabled = _settings.Current.AutoReconnect;
            Logger.SetDetailedLogging(_settings.Current.LoggingLevel == "detailed");
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _connectionDirty = true;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_uiReady || _disposed)
            {
                return;
            }

            try
            {
                if (_connectionDirty)
                {
                    ObserveRuntimeTransitions();
                    PushChanged("connection", BuildConnectionSnapshot(), ref _lastConnectionJson);
                    _connectionDirty = false;
                }

                PushChanged("metrics", BuildMetricsSnapshot(), ref _lastMetricsJson);
                PushChanged("logs", BuildLogSnapshot(), ref _lastLogsJson);
                if (_notificationsDirty)
                {
                    PushChanged("notifications", BuildNotificationSnapshot(), ref _lastNotificationsJson);
                    _notificationsDirty = false;
                }
                if (_settingsDirty)
                {
                    PushChanged("settings", BuildSettingsSnapshot(), ref _lastSettingsJson);
                    _settingsDirty = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"[NETRELAY-WEBUI] State push failed: {ex.Message}", ex);
            }
        }

        private async void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string id = string.Empty;
            string command = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "command" ||
                    !root.TryGetProperty("id", out var idElement) ||
                    !root.TryGetProperty("command", out var commandElement))
                {
                    return;
                }

                id = idElement.GetString() ?? string.Empty;
                command = commandElement.GetString() ?? string.Empty;
                var commandPayload = root.TryGetProperty("payload", out var payloadElement)
                    ? payloadElement.Clone()
                    : default;
                Logger.Info(Tag, $"[NETRELAY-WEBUI] Command received={command}");
                var payload = await ExecuteCommandAsync(command, commandPayload);
                Post(new { type = "commandResult", id, success = true, payload });
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"[NETRELAY-WEBUI] Bridge command failed={command}: {ex.Message}", ex);
                var androidError = ex as AdbBridgeService.NetRelayAndroidException;
                Post(new
                {
                    type = "commandResult",
                    id,
                    success = false,
                    errorCode = androidError?.Code ?? "COMMAND_FAILED",
                    message = androidError == null
                        ? SafeMessage(ex.Message)
                        : LocalizeAndroidError(androidError.Code),
                    payload = androidError?.Serial == null ? null : new { serial = androidError.Serial }
                });
            }
        }

        private async Task<object?> ExecuteCommandAsync(string command, JsonElement payload = default)
        {
            switch (command)
            {
                case "uiReady":
                case "getInitialState":
                    _uiReady = true;
                    var initial = new
                    {
                        connection = BuildConnectionSnapshot(),
                        metrics = BuildMetricsSnapshot(),
                        logs = BuildLogSnapshot()
                        ,settings = BuildSettingsSnapshot()
                        ,notifications = BuildNotificationSnapshot()
                    };
                    _lastConnectionJson = Serialize(initial.connection);
                    _lastMetricsJson = Serialize(initial.metrics);
                    _lastLogsJson = Serialize(initial.logs);
                    _lastNotificationsJson = Serialize(initial.notifications);
                    _lastSettingsJson = Serialize(initial.settings);
                    Push("initial", initial);
                    Logger.Info(Tag, "[NETRELAY-WEBUI] UI ready; initial state pushed");
                    NotifyUiReady();
                    return initial;

                case "getConnectionState":
                    return BuildConnectionSnapshot();
                case "getNetworkMetrics":
                    return BuildMetricsSnapshot();
                case "getRecentLogs":
                    return BuildLogSnapshot();
                case "getSettings":
                    return BuildSettingsSnapshot();
                case "getNotifications":
                    return BuildNotificationSnapshot();
                case "updateSettings":
                    ApplySettingsPayload(payload);
                    PushAllNow();
                    return BuildSettingsSnapshot();
                case "markNotificationRead":
                    _notifications.MarkRead(PayloadString(payload, "id"));
                    return BuildNotificationSnapshot();
                case "markAllNotificationsRead":
                    _notifications.MarkAllRead();
                    return BuildNotificationSnapshot();
                case "clearNotifications":
                    _notifications.Clear();
                    return BuildNotificationSnapshot();
                case "clearUiState":
                    _notifications.Clear();
                    _settings.ClearUiState();
                    return new { cleared = true };
                case "connectUsb":
                    SelectTransport("usb");
                    ApplyEndpoint(payload, "endpoint");
                    await _viewModel.ConnectAsync();
                    PushAllNow();
                    return BuildConnectionSnapshot();
                case "connectWifiDirect":
                    SelectTransport("wifiDirect");
                    ApplyWifiDirectInputs(payload);
                    await _viewModel.ConnectAsync();
                    PushAllNow();
                    return BuildConnectionSnapshot();
                case "disconnect":
                    await _viewModel.DisconnectAsync();
                    PushAllNow();
                    return BuildConnectionSnapshot();
                case "refreshUsb":
                    _usbDiscoveryAttempted = true;
                    SelectTransport("usb");
                    ExecuteIfPossible(_viewModel.DiscoverCommand);
                    return new { started = true };
                case "prepareUsbPhone":
                    SelectTransport("usb");
                    ApplyEndpoint(payload, "endpoint");
                    return await PrepareAndroidCompanionAsync(
                        PayloadString(payload, "serial"),
                        allowSignatureReset: false);
                case "reinstallAndroidApp":
                    SelectTransport("usb");
                    return await PrepareAndroidCompanionAsync(
                        PayloadString(payload, "serial"),
                        allowSignatureReset: true);
                case "refreshWifiDirect":
                    SelectTransport("wifiDirect");
                    await _viewModel.DetectWifiNetworkAsync();
                    PushAllNow();
                    return BuildConnectionSnapshot();
                case "selectWifiDirectNetwork":
                    SelectTransport("wifiDirect");
                    ApplyWifiDirectInputs(payload);
                    PushAllNow();
                    return BuildConnectionSnapshot();
                case "connectWifiNetworkOnly":
                    SelectTransport("wifiDirect");
                    ApplyWifiDirectInputs(payload);
                    await _viewModel.ConnectWifiNetworkAsync();
                    PushAllNow();
                    return BuildConnectionSnapshot();
                case "openSettingsSection":
                    return new { available = true };
                default:
                    throw new InvalidOperationException("This UI command is not allowed.");
            }
        }

        private void NotifyUiReady()
        {
            if (_disposed || _uiReadyNotified)
            {
                return;
            }

            _uiReadyNotified = true;
            _onUiReady?.Invoke();
        }

        private async Task<object> PrepareAndroidCompanionAsync(string? serial, bool allowSignatureReset)
        {
            Push("androidAction", new { state = "checking" });
            try
            {
                var prepared = await _viewModel.PreparePhoneForProductionAsync(
                    serial,
                    allowSignatureReset,
                    state => Push("androidAction", new { state }));
                PushAllNow();

                var messageKey = prepared.Outcome == "newerInstalled"
                    ? "newerInstalled"
                    : prepared.Outcome == "installed"
                        ? "installed"
                        : "opened";
                Push("toast", new
                {
                    tone = prepared.Outcome == "newerInstalled" ? "warning" : "success",
                    message = LocalizeAndroidResult(messageKey)
                });

                return new
                {
                    prepared.Serial,
                    prepared.DeviceName,
                    prepared.Outcome,
                    prepared.InstalledVersionCode,
                    prepared.InstalledVersionName
                };
            }
            finally
            {
                Push("androidAction", new { state = "idle" });
            }
        }

        private string LocalizeAndroidError(string code)
        {
            var language = _settings.Current.Language;
            return (language, code) switch
            {
                ("ar", AdbBridgeService.AndroidErrorCodes.IntegrityFailed) => "فشل التحقق من سلامة حزمة Android المضمنة.",
                ("fr", AdbBridgeService.AndroidErrorCodes.IntegrityFailed) => "La vérification de l’intégrité du package Android intégré a échoué.",
                ("ru", AdbBridgeService.AndroidErrorCodes.IntegrityFailed) => "Не удалось проверить целостность встроенного пакета Android.",
                (_, AdbBridgeService.AndroidErrorCodes.IntegrityFailed) => "The bundled Android package failed integrity verification.",

                ("ar", AdbBridgeService.AndroidErrorCodes.NoDevice) => "وصّل الهاتف عبر USB.",
                ("fr", AdbBridgeService.AndroidErrorCodes.NoDevice) => "Connectez le téléphone par USB.",
                ("ru", AdbBridgeService.AndroidErrorCodes.NoDevice) => "Подключите телефон по USB.",
                (_, AdbBridgeService.AndroidErrorCodes.NoDevice) => "Connect the phone by USB.",

                ("ar", AdbBridgeService.AndroidErrorCodes.Unauthorized) => "افتح الهاتف ووافق على تصحيح USB لهذا الكمبيوتر.",
                ("fr", AdbBridgeService.AndroidErrorCodes.Unauthorized) => "Déverrouillez le téléphone et autorisez le débogage USB pour cet ordinateur.",
                ("ru", AdbBridgeService.AndroidErrorCodes.Unauthorized) => "Разблокируйте телефон и разрешите USB-отладку для этого компьютера.",
                (_, AdbBridgeService.AndroidErrorCodes.Unauthorized) => "Unlock the phone and approve USB debugging for this computer.",

                ("ar", AdbBridgeService.AndroidErrorCodes.Offline) => "الهاتف ظاهر في ADB لكنه غير متصل. أعد توصيل الكابل ثم حاول مجددًا.",
                ("fr", AdbBridgeService.AndroidErrorCodes.Offline) => "Le téléphone apparaît dans ADB mais il est hors ligne. Reconnectez le câble et réessayez.",
                ("ru", AdbBridgeService.AndroidErrorCodes.Offline) => "Телефон виден в ADB, но находится не в сети. Переподключите кабель и повторите попытку.",
                (_, AdbBridgeService.AndroidErrorCodes.Offline) => "The phone is visible in ADB but offline. Reconnect the cable and retry.",

                ("ar", AdbBridgeService.AndroidErrorCodes.MultipleDevices) => "عُثر على أكثر من جهاز. اختر هاتفًا واحدًا ثم أعد المحاولة.",
                ("fr", AdbBridgeService.AndroidErrorCodes.MultipleDevices) => "Plusieurs appareils ont été détectés. Sélectionnez un téléphone puis réessayez.",
                ("ru", AdbBridgeService.AndroidErrorCodes.MultipleDevices) => "Обнаружено несколько устройств. Выберите один телефон и повторите попытку.",
                (_, AdbBridgeService.AndroidErrorCodes.MultipleDevices) => "Multiple devices were found. Select one phone and retry.",

                ("ar", AdbBridgeService.AndroidErrorCodes.SignatureMismatch) => "يستخدم تطبيق Android المثبت توقيعًا مختلفًا. تتطلب إعادة التثبيت إزالة التطبيق الحالي وقد تمسح بياناته المحلية.",
                ("fr", AdbBridgeService.AndroidErrorCodes.SignatureMismatch) => "L’application Android installée utilise une signature différente. La réinstallation exige sa suppression et peut effacer ses données locales.",
                ("ru", AdbBridgeService.AndroidErrorCodes.SignatureMismatch) => "Установленное Android-приложение подписано иначе. Для переустановки нужно удалить его, что может стереть локальные данные.",
                (_, AdbBridgeService.AndroidErrorCodes.SignatureMismatch) => "The installed Android app uses a different signature. Reinstallation requires removing the existing app and may erase its local data.",

                ("ar", AdbBridgeService.AndroidErrorCodes.BundledAdbMissing) => "ملفات ADB المضمنة مفقودة من تثبيت Net Relay.",
                ("fr", AdbBridgeService.AndroidErrorCodes.BundledAdbMissing) => "Les fichiers ADB intégrés sont absents de l’installation de Net Relay.",
                ("ru", AdbBridgeService.AndroidErrorCodes.BundledAdbMissing) => "В установке Net Relay отсутствуют встроенные файлы ADB.",
                (_, AdbBridgeService.AndroidErrorCodes.BundledAdbMissing) => "Bundled ADB files are missing from the Net Relay installation.",

                ("ar", _) => "فشل تثبيت تطبيق Android أو تشغيله. راجع السجلات.",
                ("fr", _) => "L’installation ou l’ouverture de l’application Android a échoué. Consultez les journaux.",
                ("ru", _) => "Не удалось установить или открыть Android-приложение. Проверьте журналы.",
                _ => "Android installation or launch failed. Check Logs for details."
            };
        }

        private string LocalizeAndroidResult(string key)
        {
            var language = _settings.Current.Language;
            return (language, key) switch
            {
                ("ar", "installed") => "تم تثبيت Net Relay Android بنجاح.",
                ("fr", "installed") => "Net Relay Android a été installé avec succès.",
                ("ru", "installed") => "Net Relay Android успешно установлен.",
                (_, "installed") => "Net Relay Android was installed successfully.",
                ("ar", "newerInstalled") => "يوجد إصدار أحدث من Net Relay Android وقد تم فتحه دون الرجوع لإصدار أقدم.",
                ("fr", "newerInstalled") => "Une version plus récente de Net Relay Android est déjà installée et a été ouverte sans rétrogradation.",
                ("ru", "newerInstalled") => "Уже установлена более новая версия Net Relay Android; она открыта без понижения версии.",
                (_, "newerInstalled") => "A newer Net Relay Android version is already installed and was opened without downgrading.",
                ("ar", _) => "تم فتح Net Relay Android.",
                ("fr", _) => "Net Relay Android a été ouvert.",
                ("ru", _) => "Net Relay Android открыт.",
                _ => "Net Relay Android was opened."
            };
        }

        private object BuildSettingsSnapshot()
        {
            var value = _settings.Current;
            return new
            {
                language = value.Language,
                theme = value.Theme,
                startWithWindows = value.StartWithWindows,
                startMinimized = value.StartMinimized,
                closeToTray = value.CloseToTray,
                autoReconnect = value.AutoReconnect,
                preferredTransport = value.PreferredTransport,
                restoreLastPage = value.RestoreLastPage,
                lastPage = value.LastPage,
                loggingLevel = value.LoggingLevel,
                maxUiLogs = value.MaxUiLogs
            };
        }

        private void ApplySettingsPayload(JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object) return;
            _settings.Update(value =>
            {
                if (TryPayloadString(payload, "language", out var language)) value.Language = language;
                if (TryPayloadString(payload, "theme", out var theme)) value.Theme = theme;
                if (TryPayloadBool(payload, "startWithWindows", out var startWithWindows)) value.StartWithWindows = startWithWindows;
                if (TryPayloadBool(payload, "startMinimized", out var startMinimized)) value.StartMinimized = startMinimized;
                if (TryPayloadBool(payload, "closeToTray", out var closeToTray)) value.CloseToTray = closeToTray;
                if (TryPayloadBool(payload, "autoReconnect", out var autoReconnect)) value.AutoReconnect = autoReconnect;
                if (TryPayloadString(payload, "preferredTransport", out var preferredTransport)) value.PreferredTransport = preferredTransport;
                if (TryPayloadBool(payload, "restoreLastPage", out var restoreLastPage)) value.RestoreLastPage = restoreLastPage;
                if (TryPayloadString(payload, "lastPage", out var lastPage)) value.LastPage = lastPage;
                if (TryPayloadString(payload, "loggingLevel", out var loggingLevel)) value.LoggingLevel = loggingLevel;
                if (TryPayloadInt(payload, "maxUiLogs", out var maxUiLogs)) value.MaxUiLogs = maxUiLogs;
            });
        }

        private object BuildNotificationSnapshot()
        {
            var items = _notifications.Snapshot();
            return new
            {
                unreadCount = items.Count(item => !item.IsRead),
                items = items.Select(item => new
                {
                    id = item.Id,
                    timestamp = item.Timestamp,
                    severity = item.Severity,
                    category = item.Category,
                    titleKey = item.TitleKey,
                    messageKey = item.MessageKey,
                    messageArgs = item.MessageArgs,
                    isRead = item.IsRead
                }).ToArray()
            };
        }

        private void ObserveRuntimeTransitions()
        {
            var connected = _viewModel.IsConnected;
            var internet = connected && _viewModel.IsAdapterModeRunning;
            var uiState = _viewModel.UiState.ToString().ToLowerInvariant();
            var usbReady = _viewModel.DiscoveredDevices.Any(device =>
                string.Equals(device.TransportType, "usb", StringComparison.OrdinalIgnoreCase));
            var adbStatus = GetUsbAdbStatus(usbReady);

            if (!_transitionInitialized)
            {
                _transitionInitialized = true;
                _previousConnected = connected;
                _previousInternet = internet;
                _previousUiState = uiState;
                _previousAdbStatus = adbStatus;
                return;
            }

            if (!_previousConnected && connected)
            {
                var transport = _viewModel.CurrentDevice?.TransportType ?? _viewModel.CurrentTransport ?? "Net Relay";
                _notifications.Add("success", "connection", "notificationConnectedTitle", "notificationConnectedMessage",
                    new() { ["transport"] = transport });
            }
            else if (_previousConnected && !connected)
            {
                var manual = (_viewModel.StatusMessage ?? string.Empty).Contains("تم قطع", StringComparison.OrdinalIgnoreCase);
                _notifications.Add(manual ? "info" : "warning", "connection",
                    manual ? "notificationDisconnectedTitle" : "notificationConnectionLostTitle",
                    manual ? "notificationDisconnectedMessage" : "notificationConnectionLostMessage");
            }

            if (!_previousInternet && internet && connected)
            {
                _notifications.Add("success", "internet", "notificationInternetTitle", "notificationInternetAvailableMessage");
            }
            else if (_previousInternet && !internet && connected)
            {
                _notifications.Add("warning", "internet", "notificationInternetLostTitle", "notificationInternetLostMessage");
            }

            if (_previousAdbStatus != "unauthorized" && adbStatus == "unauthorized")
            {
                _notifications.Add("warning", "usb", "notificationAdbTitle", "notificationAdbUnauthorizedMessage");
            }

            if (_previousUiState != "reconnecting" && uiState == "reconnecting")
            {
                _notifications.Add("info", "connection", "notificationRecoveryTitle", "notificationRecoveryMessage");
            }
            if (_previousUiState != "error" && uiState == "error")
            {
                _notifications.Add("error", "system", "notificationErrorTitle", "notificationErrorMessage");
            }

            _previousConnected = connected;
            _previousInternet = internet;
            _previousUiState = uiState;
            _previousAdbStatus = adbStatus;
        }

        private void ApplyEndpoint(JsonElement payload, string propertyName)
        {
            var endpoint = PayloadString(payload, propertyName);
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                _viewModel.TargetEndpoint = endpoint;
            }
        }

        private void ApplyWifiDirectInputs(JsonElement payload)
        {
            var ssid = PayloadString(payload, "ssid");
            if (!string.IsNullOrWhiteSpace(ssid))
            {
                _viewModel.SelectedWifiDirectNetworkName = ssid;
                _viewModel.WifiDirectNetworkName = ssid;
            }

            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("passphrase", out var passphrase))
            {
                _viewModel.WifiDirectPassphrase = passphrase.GetString() ?? string.Empty;
            }

            ApplyEndpoint(payload, "endpoint");
        }

        private static string PayloadString(JsonElement payload, string propertyName)
        {
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString()?.Trim() ?? string.Empty;
        }

        private void SelectTransport(string key)
        {
            var option = _viewModel.TransportOptions.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (option == null || !option.IsImplemented)
            {
                throw new InvalidOperationException($"Transport {key} is not available.");
            }

            _viewModel.SelectedTransportOption = option;
        }

        private void PushAllNow()
        {
            _connectionDirty = true;
            Timer_Tick(null, EventArgs.Empty);
        }

        private object BuildConnectionSnapshot()
        {
            if (_viewModel.IsConnected)
            {
                _connectedSince ??= DateTimeOffset.UtcNow;
            }
            else
            {
                _connectedSince = null;
            }

            var selected = _viewModel.SelectedTransportOption?.Key ?? string.Empty;
            var active = _viewModel.IsConnected
                ? _viewModel.CurrentDevice?.TransportType ?? string.Empty
                : string.Empty;
            var endpointVisible = _viewModel.IsConnected || _viewModel.IsConnecting;
            var settings = _settings.Current;
            var usbActive = _viewModel.IsConnected && string.Equals(active, "usb", StringComparison.OrdinalIgnoreCase);
            var usbConnecting = _viewModel.IsConnecting && string.Equals(selected, "usb", StringComparison.OrdinalIgnoreCase);
            var usbDevice = usbActive
                ? _viewModel.CurrentDevice
                : _viewModel.DiscoveredDevices.FirstOrDefault(device =>
                    string.Equals(device.TransportType, "usb", StringComparison.OrdinalIgnoreCase));
            var usbAdbStatus = GetUsbAdbStatus(usbDevice != null || usbActive);
            var wifiActive = _viewModel.IsConnected && string.Equals(active, "wifiDirect", StringComparison.OrdinalIgnoreCase);
            var wifiConnecting = _viewModel.IsConnecting && string.Equals(selected, "wifiDirect", StringComparison.OrdinalIgnoreCase);
            var wifiLive = wifiActive || wifiConnecting;
            var wifiPhoneIp = wifiLive ? EndpointHost(_viewModel.WifiDirectControlEndpoint) : string.Empty;

            return new
            {
                appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
                language = settings.Language,
                theme = settings.Theme,
                uiState = _viewModel.UiState.ToString().ToLowerInvariant(),
                statusMessage = UiStatusMessage(settings.Language, _viewModel.UiState),
                isConnected = _viewModel.IsConnected,
                isConnecting = _viewModel.IsConnecting,
                selectedTransport = selected,
                activeTransport = active,
                connectionDuration = FormatDuration(),
                phone = new
                {
                    name = _viewModel.IsConnected ? _viewModel.CurrentDevice?.DeviceName ?? UiText(settings.Language, "androidPhone") : UiText(settings.Language, "noPhone"),
                    id = _viewModel.IsConnected ? _viewModel.CurrentDevice?.DeviceId ?? string.Empty : string.Empty,
                    status = UiText(settings.Language, _viewModel.IsConnected ? "connected" : "disconnected")
                },
                internet = new
                {
                    available = _viewModel.IsConnected && _viewModel.IsAdapterModeRunning,
                    status = UiText(settings.Language, _viewModel.IsConnected && _viewModel.IsAdapterModeRunning ? "connected" : "disconnected"),
                    detail = UiText(settings.Language, _viewModel.IsConnected && _viewModel.IsAdapterModeRunning ? "internetAvailable" : _viewModel.IsConnected ? "internetUnavailable" : "noPhoneConnection")
                },
                adapter = new
                {
                    running = _viewModel.IsAdapterModeRunning,
                    status = UiText(settings.Language, _viewModel.IsAdapterModeRunning ? "active" : "inactive")
                },
                endpoints = new
                {
                    target = endpointVisible ? _viewModel.TargetEndpoint : string.Empty,
                    proxy = endpointVisible ? _viewModel.ProxyEndpoint : string.Empty,
                    gateway = endpointVisible ? _viewModel.GatewayEndpoint : string.Empty,
                    socks = "127.0.0.1:8990",
                    control = ControlEndpoint(active.Length > 0 ? active : selected)
                },
                usb = new
                {
                    cableStatus = UsbCableStatus(usbAdbStatus),
                    debuggingStatus = UsbDebuggingStatus(usbAdbStatus),
                    adbStatus = usbAdbStatus,
                    authorization = usbAdbStatus == "ready"
                        ? "authorized"
                        : usbAdbStatus == "unauthorized" ? "unauthorized" : "unknown",
                    deviceName = usbDevice?.DeviceName ?? string.Empty,
                    deviceId = usbDevice?.DeviceId ?? string.Empty,
                    controlEndpoint = _viewModel.UsbControlEndpoint,
                    gatewayEndpoint = usbActive || usbConnecting
                        ? $"127.0.0.1:{AdbBridgeService.LocalDataTunnelPort} → Android :{AdbBridgeService.RemoteGatewayPort}"
                        : string.Empty
                },
                wifiDirect = new
                {
                    status = wifiActive
                        ? "connected"
                        : wifiConnecting ? "connecting"
                        : _viewModel.UiState == UiState.Error && string.Equals(selected, "wifiDirect", StringComparison.OrdinalIgnoreCase)
                            ? "error"
                            : "inactive",
                    groupFormed = wifiActive ? true : (bool?)null,
                    isGroupOwner = (bool?)null,
                    groupOwner = string.Empty,
                    networkName = _viewModel.WifiDirectNetworkName,
                    passphrase = string.Empty,
                    phoneIp = wifiPhoneIp,
                    windowsIp = _viewModel.WifiDirectWindowsIp,
                    profileStatus = UiWifiProfileStatus(settings.Language, _viewModel.WifiDirectHasSavedProfile),
                    hasSavedProfile = _viewModel.WifiDirectHasSavedProfile,
                    isSearching = _viewModel.IsDetectingWifiNetworks,
                    controlEndpoint = _viewModel.WifiDirectControlEndpoint,
                    gatewayEndpoint = string.IsNullOrWhiteSpace(wifiPhoneIp)
                        ? string.Empty
                        : $"{wifiPhoneIp}:{NetRelayDataPlaneService.AndroidGatewayPort}",
                    availableNetworks = _viewModel.WifiDirectNetworkNames.Take(30).ToArray()
                },
                transports = _viewModel.TransportOptions
                    .Where(option => option.Key is "usb" or "wifiDirect")
                    .Select(option => new
                    {
                        key = option.Key,
                        label = option.Key == "usb" ? "USB" : "Wi‑Fi Direct",
                        state = TransportState(option.Key, selected, active),
                        endpoint = ControlEndpoint(option.Key),
                        available = option.IsImplemented
                    })
                    .ToArray(),
                notificationsCount = _notifications.Snapshot().Count(item => !item.IsRead),
                appInfo = new
                {
                    product = "Net Relay",
                    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
                    ui = "Chromium / WebView2",
                    runtime = RuntimeInformation.FrameworkDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    webView2Version = SafeWebViewVersion(),
                    appDataPath = AppSettingsService.SettingsDirectory
                }
            };
        }

        private object BuildMetricsSnapshot()
        {
            var connected = _viewModel.IsConnected && _viewModel.IsAdapterModeRunning;
            return new
            {
                download = connected ? _viewModel.DownloadSpeedText : "0 bps",
                upload = connected ? _viewModel.UploadSpeedText : "0 bps",
                ping = "-- ms",
                totalReceived = _viewModel.DownloadTotalText,
                totalSent = _viewModel.UploadTotalText,
                duration = connected ? FormatMetricsDuration() : "--:--",
                downloadBps = connected ? _viewModel.DownloadBitsPerSecond : 0,
                uploadBps = connected ? _viewModel.UploadBitsPerSecond : 0,
                totalReceivedBytes = _viewModel.SessionReceivedBytes,
                totalSentBytes = _viewModel.SessionSentBytes,
                activeTcpFlows = connected ? _viewModel.ActiveTcpFlows : 0,
                activeUdpFlows = connected ? _viewModel.ActiveUdpFlows : 0,
                totalTcpFlows = _viewModel.TotalTcpFlows,
                totalUdpFlows = _viewModel.TotalUdpFlows,
                hasActiveSession = connected
            };
        }

        private object[] BuildLogSnapshot()
        {
            return Logger.GetHistory()
                .TakeLast(_settings.Current.MaxUiLogs)
                .Select(entry => (object)new
                {
                    timestamp = entry.Timestamp,
                    level = entry.Level,
                    tag = SafeMessage(entry.Tag),
                    message = SafeMessage(entry.Message)
                })
                .ToArray();
        }

        private string TransportState(string key, string selected, string active)
        {
            if (_viewModel.IsConnected && string.Equals(active, key, StringComparison.OrdinalIgnoreCase))
            {
                return "active";
            }

            if (_viewModel.IsConnecting && string.Equals(selected, key, StringComparison.OrdinalIgnoreCase))
            {
                return "connecting";
            }

            if (_viewModel.UiState == UiState.Error && string.Equals(selected, key, StringComparison.OrdinalIgnoreCase))
            {
                return "error";
            }

            return "inactive";
        }

        private string ControlEndpoint(string transport)
        {
            return string.Equals(transport, "usb", StringComparison.OrdinalIgnoreCase)
                ? _viewModel.UsbControlEndpoint
                : string.Equals(transport, "wifiDirect", StringComparison.OrdinalIgnoreCase)
                    ? _viewModel.WifiDirectControlEndpoint
                    : string.Empty;
        }

        private string GetUsbAdbStatus(bool readyDevicePresent)
        {
            if (readyDevicePresent)
            {
                return "ready";
            }

            if (!_usbDiscoveryAttempted)
            {
                return "unknown";
            }

            var status = _viewModel.StatusMessage ?? string.Empty;
            if (status.Contains("غير مصرح", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return "unauthorized";
            }

            if (status.Contains("Offline", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("غير جاهزة", StringComparison.OrdinalIgnoreCase))
            {
                return "offline";
            }

            return _viewModel.IsDiscovering ? "unknown" : "notFound";
        }

        private string UsbCableStatus(string adbStatus)
        {
            return adbStatus switch
            {
                "ready" or "unauthorized" or "offline" => "connected",
                "notFound" when _usbDiscoveryAttempted => "disconnected",
                _ => "unknown"
            };
        }

        private static string UsbDebuggingStatus(string adbStatus)
        {
            return adbStatus is "ready" or "unauthorized" or "offline"
                ? "enabled"
                : "unknown";
        }

        private static string EndpointHost(string endpoint)
        {
            var value = endpoint?.Trim() ?? string.Empty;
            if (Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }

            return string.Empty;
        }

        private string FormatDuration()
        {
            if (_connectedSince == null)
            {
                return "--:--";
            }

            var elapsed = DateTimeOffset.UtcNow - _connectedSince.Value;
            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        private string FormatMetricsDuration()
        {
            if (_viewModel.MetricsSessionStartedAt == null) return "--:--";
            var elapsed = DateTimeOffset.UtcNow - _viewModel.MetricsSessionStartedAt.Value;
            return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        private static string SafeWebViewVersion()
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return "Unavailable"; }
        }

        private static string UiStatusMessage(string language, UiState state) => state switch
        {
            UiState.Connected => UiText(language, "connected"),
            UiState.Connecting => UiText(language, "connecting"),
            UiState.Reconnecting => UiText(language, "reconnecting"),
            UiState.Error => UiText(language, "operationFailed"),
            _ => UiText(language, "ready")
        };

        private static string UiWifiProfileStatus(string language, bool? hasSavedProfile)
        {
            var key = hasSavedProfile == true ? "savedProfile" : hasSavedProfile == false ? "noSavedProfile" : "profileUnknown";
            return language switch
            {
                "ar" => key switch { "savedProfile" => "سيتم استخدام ملف WLAN المحفوظ.", "noSavedProfile" => "لا يوجد ملف WLAN محفوظ.", _ => "حالة ملف WLAN غير متاحة." },
                "fr" => key switch { "savedProfile" => "Le profil WLAN enregistré sera utilisé.", "noSavedProfile" => "Aucun profil WLAN enregistré.", _ => "État du profil WLAN indisponible." },
                "ru" => key switch { "savedProfile" => "Будет использован сохранённый профиль WLAN.", "noSavedProfile" => "Сохранённый профиль WLAN отсутствует.", _ => "Состояние профиля WLAN недоступно." },
                _ => key switch { "savedProfile" => "The saved WLAN profile will be used.", "noSavedProfile" => "No saved WLAN profile is available.", _ => "WLAN profile state is unavailable." }
            };
        }

        private static string UiText(string language, string key)
        {
            return language switch
            {
                "ar" => key switch { "connected" => "متصل", "disconnected" => "غير متصل", "connecting" => "جارٍ الاتصال…", "reconnecting" => "جارٍ استعادة الاتصال…", "active" => "نشط", "inactive" => "غير نشط", "androidPhone" => "هاتف Android", "noPhone" => "لا يوجد هاتف", "internetAvailable" => "تمرير الإنترنت نشط.", "internetUnavailable" => "تمرير الإنترنت غير متاح.", "noPhoneConnection" => "لا يوجد اتصال هاتف.", "operationFailed" => "فشلت العملية. راجع السجلات.", _ => "جاهز" },
                "fr" => key switch { "connected" => "Connecté", "disconnected" => "Non connecté", "connecting" => "Connexion…", "reconnecting" => "Reconnexion…", "active" => "Actif", "inactive" => "Inactif", "androidPhone" => "Téléphone Android", "noPhone" => "Aucun téléphone", "internetAvailable" => "Le relais Internet est actif.", "internetUnavailable" => "Le relais Internet est indisponible.", "noPhoneConnection" => "Aucune connexion au téléphone.", "operationFailed" => "L’opération a échoué. Consultez les journaux.", _ => "Prêt" },
                "ru" => key switch { "connected" => "Подключено", "disconnected" => "Не подключено", "connecting" => "Подключение…", "reconnecting" => "Переподключение…", "active" => "Активно", "inactive" => "Неактивно", "androidPhone" => "Телефон Android", "noPhone" => "Телефон не найден", "internetAvailable" => "Перенаправление Интернета активно.", "internetUnavailable" => "Перенаправление Интернета недоступно.", "noPhoneConnection" => "Нет подключения к телефону.", "operationFailed" => "Операция не выполнена. Проверьте журналы.", _ => "Готово" },
                _ => key switch { "connected" => "Connected", "disconnected" => "Not connected", "connecting" => "Connecting…", "reconnecting" => "Reconnecting…", "active" => "Active", "inactive" => "Inactive", "androidPhone" => "Android phone", "noPhone" => "No phone", "internetAvailable" => "Internet forwarding is active.", "internetUnavailable" => "Internet forwarding is unavailable.", "noPhoneConnection" => "No phone connection.", "operationFailed" => "The operation failed. Check Logs.", _ => "Ready" }
            };
        }

        private static double ParseBitsPerSecond(string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            return parts[1].ToLowerInvariant() switch
            {
                "gbps" => value * 1_000_000_000,
                "mbps" => value * 1_000_000,
                "kbps" => value * 1_000,
                _ => value
            };
        }

        private static bool TryPayloadString(JsonElement payload, string propertyName, out string value)
        {
            value = string.Empty;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String) return false;
            value = element.GetString()?.Trim() ?? string.Empty;
            return true;
        }

        private static bool TryPayloadBool(JsonElement payload, string propertyName, out bool value)
        {
            value = false;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var element) ||
                (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
            {
                return false;
            }
            value = element.GetBoolean();
            return true;
        }

        private static bool TryPayloadInt(JsonElement payload, string propertyName, out int value)
        {
            value = 0;
            return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out var element) && element.TryGetInt32(out value);
        }

        private static void ExecuteIfPossible(ICommand command)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        }

        private void PushChanged(string topic, object payload, ref string previousJson)
        {
            var json = Serialize(payload);
            if (string.Equals(json, previousJson, StringComparison.Ordinal))
            {
                return;
            }

            previousJson = json;
            Push(topic, payload);
        }

        private void Push(string topic, object payload)
        {
            Post(new { type = "state", topic, payload });
        }

        private void Post(object message)
        {
            if (_disposed)
            {
                return;
            }

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(() => Post(message));
                return;
            }

            _webView.PostWebMessageAsJson(Serialize(message));
        }

        private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

        private static string SafeMessage(string value)
        {
            var redacted = SecretPattern.Replace(value ?? string.Empty, "$1=[redacted]");
            return redacted.Length <= 2_000 ? redacted : redacted[..2_000] + "…";
        }
    }
}
