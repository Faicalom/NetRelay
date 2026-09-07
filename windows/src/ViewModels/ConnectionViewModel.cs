using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NetRelay.Models;
using NetRelay.Protocol;
using NetRelay.Services;
using NetRelay.Transport;
using NetRelay.Transport.Usb;
using NetRelay.Transport.WiFi;
using NetRelay.Utils;

namespace NetRelay.ViewModels
{
    public partial class ConnectionViewModel : INotifyPropertyChanged
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "ConnectionViewModel";

        private readonly TransportManager _transportManager = new();
        private readonly SessionManager _sessionManager = new();
        private readonly AdbBridgeService _adbBridgeService = new();
        private readonly WifiNetworkScanService _wifiNetworkScanService = new();
        private readonly MessageSerializer _serializer = new();
        private readonly ProtocolHandler _protocolHandler;

        private UiState _uiState = UiState.Idle;
        private string? _statusMessage;
        private bool _isConnecting;
        private bool _isDiscovering;
        private Device? _currentDevice;
        private string? _currentTransport;
        private TransportOption? _selectedTransportOption;
        private Device? _selectedDiscoveredDevice;
        private string _targetEndpoint = string.Empty;
        private string _usbEndpoint = "127.0.0.1:8988";
        private string _wifiDirectEndpoint = DefaultWifiDirectEndpoint;
        private string _wifiDirectNetworkName = string.Empty;
        private string _wifiDirectPassphrase = string.Empty;
        private string _wifiDirectProfileStatus = "سيُستخدم WLAN Profile المحفوظ تلقائيًا إن وُجد";
        private bool? _wifiDirectHasSavedProfile;
        private string _wifiDirectWindowsIp = string.Empty;
        private string? _selectedWifiDirectNetworkName;
        private string _logOutput = string.Empty;
        private bool _isDetectingWifiNetworks;
        private CancellationTokenSource? _wifiNetworkScanCancellation;
        private bool _activeWifiNetworkScanRequested;
        private IDisposable? _incomingSubscription;
        private IDisposable? _stateSubscription;
        private IDisposable? _protocolSubscription;
        private CancellationTokenSource? _heartbeatCancellation;
        private int _missedHeartbeats;
        private readonly RelayCommand _connectCommand;
        private readonly RelayCommand _disconnectCommand;
        private readonly RelayCommand _discoverCommand;
        private readonly RelayCommand _preparePhoneCommand;
        private readonly RelayCommand _detectWifiNetworkCommand;
        private readonly RelayCommand _connectWifiNetworkCommand;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionViewModel()
        {
            _protocolHandler = new ProtocolHandler(_serializer);
            _connectCommand = new RelayCommand(() => _ = ConnectAsync(), () => !IsConnecting);
            _disconnectCommand = new RelayCommand(
                () => _ = DisconnectAsync(),
                () => IsConnected || IsConnecting || IsWifiDirectSelectedOrActive());
            _discoverCommand = new RelayCommand(() => _ = DiscoverDevicesAsync(), () => !IsDiscovering);
            _preparePhoneCommand = new RelayCommand(() => _ = PreparePhoneAsync(), () => !IsConnecting && !IsDiscovering);
            _detectWifiNetworkCommand = new RelayCommand(
                () => _ = DetectWifiNetworkAsync(),
                () => !IsDetectingWifiNetworks);
            _connectWifiNetworkCommand = new RelayCommand(
                () => _ = ConnectWifiNetworkAsync(),
                () => !IsDetectingWifiNetworks);
            InitializeAdapterMode();
            RefreshCommand = new RelayCommand(RefreshDiagnostics);
            ClearLogsCommand = new RelayCommand(ClearLogs);

            InitializeTransports();
            _ = DetectWifiNetworkAsync(updateStatus: false, requestActiveScan: false);
            SubscribeToProtocolMessages();
            StatusMessage = "جاهز للبحث عن الهاتف عبر Wi-Fi Direct أو USB";
            RefreshDiagnostics();
            StartNetworkMetrics();
            Logger.Info(Tag, "ConnectionViewModel initialized");
        }

        public UiState UiState
        {
            get => _uiState;
            set
            {
                if (SetProperty(ref _uiState, value))
                {
                    OnPropertyChanged(nameof(IsConnected));
                    OnPropertyChanged(nameof(IsNotConnected));
                }
            }
        }

        public string? StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsConnecting
        {
            get => _isConnecting;
            set => SetProperty(ref _isConnecting, value);
        }

        public bool IsDiscovering
        {
            get => _isDiscovering;
            set => SetProperty(ref _isDiscovering, value);
        }

        public Device? CurrentDevice
        {
            get => _currentDevice;
            set => SetProperty(ref _currentDevice, value);
        }

        public string? CurrentTransport
        {
            get => _currentTransport;
            set => SetProperty(ref _currentTransport, value);
        }

        public TransportOption? SelectedTransportOption
        {
            get => _selectedTransportOption;
            set
            {
                if (SetProperty(ref _selectedTransportOption, value))
                {
                    if (value != null)
                    {
                        StatusMessage = $"الوضع المختار: {value.DisplayName}";
                    }
                    if (value != null)
                    {
                        if (string.Equals(value.Key, "wifiDirect", StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Info(Tag, "[NETRELAY-WFD] Transport selected=wifiDirect");
                            Logger.Info(Tag, "[NETRELAY-WFD] Previous transport endpoint ignored");
                            ApplyEndpointForTransport("wifiDirect");
                            _ = DetectWifiNetworkAsync(updateStatus: false, requestActiveScan: false);
                        }
                        else if (string.Equals(value.Key, "usb", StringComparison.OrdinalIgnoreCase))
                        {
                            CancelWifiNetworkScan();
                            ApplyEndpointForTransport("usb");
                            ProxyEndpoint = string.Empty;
                            GatewayEndpoint = string.Empty;
                            SelectedDiscoveredDevice = null;
                        }
                    }

                    RefreshDiagnostics();
                }
            }
        }

        public Device? SelectedDiscoveredDevice
        {
            get => _selectedDiscoveredDevice;
            set
            {
                if (SetProperty(ref _selectedDiscoveredDevice, value) && value != null)
                {
                    var selectedTransport = SelectedTransportOption?.Key;
                    if (!string.IsNullOrWhiteSpace(selectedTransport) &&
                        !string.Equals(value.TransportType, selectedTransport, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info(Tag, $"Ignoring {value.TransportType} endpoint while {selectedTransport} is selected");
                        return;
                    }
                    TargetEndpoint = value.DeviceId;
                    ProxyEndpoint = value.ProxyEndpoint;
                    GatewayEndpoint = value.GatewayEndpoint;
                }
            }
        }

        public string TargetEndpoint
        {
            get => _targetEndpoint;
            set
            {
                var normalized = value?.Trim() ?? string.Empty;
                RememberEndpointForTransport(SelectedTransportOption?.Key, normalized);
                SetProperty(ref _targetEndpoint, normalized);
            }
        }

        public string UsbControlEndpoint => _usbEndpoint;
        public string WifiDirectControlEndpoint => _wifiDirectEndpoint;

        public string WifiDirectNetworkName
        {
            get => _wifiDirectNetworkName;
            set
            {
                if (SetProperty(ref _wifiDirectNetworkName, value))
                {
                    _connectWifiNetworkCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string WifiDirectPassphrase
        {
            get => _wifiDirectPassphrase;
            set
            {
                if (SetProperty(ref _wifiDirectPassphrase, value))
                {
                    _connectWifiNetworkCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string WifiDirectProfileStatus
        {
            get => _wifiDirectProfileStatus;
            set => SetProperty(ref _wifiDirectProfileStatus, value);
        }

        public bool? WifiDirectHasSavedProfile
        {
            get => _wifiDirectHasSavedProfile;
            private set => SetProperty(ref _wifiDirectHasSavedProfile, value);
        }

        public string WifiDirectWindowsIp
        {
            get => _wifiDirectWindowsIp;
            private set => SetProperty(ref _wifiDirectWindowsIp, value);
        }

        public string? SelectedWifiDirectNetworkName
        {
            get => _selectedWifiDirectNetworkName;
            set
            {
                if (SetProperty(ref _selectedWifiDirectNetworkName, value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    WifiDirectNetworkName = value;
                }
            }
        }

        public bool IsDetectingWifiNetworks
        {
            get => _isDetectingWifiNetworks;
            set
            {
                if (SetProperty(ref _isDetectingWifiNetworks, value))
                {
                    _detectWifiNetworkCommand.RaiseCanExecuteChanged();
                    _connectWifiNetworkCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string LogOutput
        {
            get => _logOutput;
            set => SetProperty(ref _logOutput, value);
        }

        public bool IsConnected => UiState == UiState.Connected;
        public bool IsNotConnected => !IsConnected;

        public ObservableCollection<string> AvailableTransports { get; } = new();
        public ObservableCollection<TransportOption> TransportOptions { get; } = new();
        public ObservableCollection<Device> DiscoveredDevices { get; } = new();
        public ObservableCollection<Device> PairedDevices { get; } = new();
        public ObservableCollection<string> WifiDirectNetworkNames { get; } = new();
        public ICommand ConnectCommand => _connectCommand;
        public ICommand DisconnectCommand => _disconnectCommand;
        public ICommand DiscoverCommand => _discoverCommand;
        public ICommand PreparePhoneCommand => _preparePhoneCommand;
        public ICommand DetectWifiNetworkCommand => _detectWifiNetworkCommand;
        public ICommand ConnectWifiNetworkCommand => _connectWifiNetworkCommand;
        public ICommand RefreshCommand { get; }
        public ICommand ClearLogsCommand { get; }

        public void RegisterTransport(string transportType, ITransport transport)
        {
            _transportManager.RegisterTransport(transportType, transport);
            AvailableTransports.Add(transportType);
            Logger.Debug(Tag, $"Transport registered: {transportType}");
        }

        public async Task ConnectToDeviceAsync(Device device, string transportType)
        {
            if (!TryBeginSerializedConnectionAttempt(out var connectionGeneration))
            {
                Logger.Info(Tag, "[NETRELAY-CONNECT] Duplicate connect ignored; connection already in progress");
                StatusMessage = "محاولة اتصال قيد التنفيذ بالفعل";
                return;
            }
            BeginUserConnectionAttempt();
            Logger.Info(Tag, $"[NETRELAY-USB] Connect command entered");
            Logger.Info(Tag, $"[NETRELAY-USB] Requested transport = {transportType}");
            try
            {
                IsConnecting = true;
                UiState = UiState.Connecting;
                StatusMessage = $"جارٍ الاتصال مع {device.DeviceName}...";
                CurrentDevice = device;
                CurrentTransport = TransportOptions.FirstOrDefault(option => option.Key == transportType)?.DisplayName
                    ?? transportType;

                if (!_transportManager.SetActiveTransport(transportType))
                {
                    UiState = UiState.Error;
                    StatusMessage = "وضع الاتصال غير متاح";
                    return;
                }

                var sessionId = _sessionManager.InitiateSession(device);
                Logger.Info(Tag, $"Session initiated: {sessionId}");

                if (string.Equals(transportType, "usb", StringComparison.OrdinalIgnoreCase))
                    Logger.Info(Tag, "[NETRELAY-USB] Calling UsbTransport.ConnectAsync");
                var connected = await _transportManager.ConnectAsync(device.DeviceId);
                EnsureCurrentConnectionAttempt(connectionGeneration);
                if (!connected)
                {
                    UiState = UiState.Error;
                    StatusMessage = "فشل الاتصال";
                    return;
                }

                SubscribeToActiveTransport();
                _sessionManager.StartHandshake();
                await SendHandshakeAsync(sessionId);
                EnsureCurrentConnectionAttempt(connectionGeneration);
                await PrepareAndApplyProxyAsync();
                EnsureCurrentConnectionAttempt(connectionGeneration);
                if (!IsAdapterModeRunning || !_dataPlaneService.IsRunning)
                    throw new InvalidOperationException("AEL2 data plane validation failed before ACTIVE commit.");
                _sessionManager.MarkActive();
                RememberSuccessfulConnection(device, transportType);
                StartHeartbeatMonitor();
                UiState = UiState.Connected;
                StatusMessage = $"تم الاتصال مع {device.DeviceName}";
                Logger.Info(Tag, $"Connected to {device.DeviceName}");
            }
            catch (Exception ex)
            {
                if (!IsCurrentConnectionAttempt(connectionGeneration))
                {
                    Logger.Info(Tag, $"[NETRELAY-CONNECT] Stale completion ignored generation={connectionGeneration}");
                    return;
                }
                if (string.Equals(transportType, "usb", StringComparison.OrdinalIgnoreCase))
                    Logger.Error(Tag, $"[NETRELAY-USB] FAILED stage=ConnectionViewModel.ConnectToDeviceAsync type={ex.GetType().FullName} message={ex.Message}\n{ex.StackTrace}", ex);
                Logger.Error(Tag, $"Connection error: {ex.Message}", ex);
                UiState = UiState.Error;
                StatusMessage = $"خطأ: {ex.Message}";
                try
                {
                    await StopAdapterModeInternalAsync(updateProxyStatus: false);
                    await _transportManager.DisconnectAsync();
                    await _sessionManager.CloseSessionAsync();
                }
                catch (Exception rollbackError)
                {
                    Logger.Warn(Tag, $"Connection rollback failed: {rollbackError.Message}");
                }
            }
            finally
            {
                IsConnecting = false;
                RefreshDiagnostics();
                EndSerializedConnectionAttempt(connectionGeneration);
            }
        }

        public async Task DisconnectAsync()
        {
            MarkManualDisconnect();
            var disconnectWifiNetwork = IsWifiDirectSelectedOrActive();
            Exception? cleanupError = null;
            try
            {
                StopHeartbeatMonitor();
                await _sessionManager.CloseSessionAsync();
                await StopAdapterModeInternalAsync(updateProxyStatus: false);
                await _transportManager.DisconnectAsync();
                await RestoreWindowsProxyAsync();
                DisposeTransportSubscriptions();
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Disconnect error: {ex.Message}", ex);
                cleanupError = ex;
            }

            if (disconnectWifiNetwork)
            {
                try
                {
                    var wlanDisconnected = await _wifiNetworkScanService.DisconnectFromNetworkAsync(
                        WifiDirectNetworkName);
                    if (!wlanDisconnected)
                    {
                        cleanupError ??= new InvalidOperationException(
                            "تعذر فصل Windows عن شبكة Wi-Fi Direct.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(Tag, $"Wi-Fi Direct WLAN disconnect error: {ex.Message}", ex);
                    cleanupError ??= ex;
                }
            }

            try
            {
                UiState = cleanupError == null ? UiState.Disconnected : UiState.Error;
                StatusMessage = cleanupError == null
                    ? disconnectWifiNetwork
                        ? "تم قطع NetRelay وفصل شبكة Wi-Fi Direct مع الاحتفاظ بالشبكة المحفوظة"
                        : "تم قطع الاتصال"
                    : $"تم تنفيذ القطع مع خطأ: {cleanupError.Message}";
                CurrentDevice = null;
                CurrentTransport = null;
                RefreshDiagnostics();
            }
            finally
            {
                IsConnecting = false;
                _connectCommand.RaiseCanExecuteChanged();
                _disconnectCommand.RaiseCanExecuteChanged();
            }
        }

        public async Task SendDataAsync(byte[] data)
        {
            try
            {
                if (!await _transportManager.SendAsync(data))
                {
                    Logger.Error(Tag, "Failed to send data");
                    StatusMessage = "فشل إرسال البيانات";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Send error: {ex.Message}", ex);
            }
        }

        public void LoadPairedDevices()
        {
            PairedDevices.Clear();
            foreach (var device in _sessionManager.GetPairedDevices())
            {
                PairedDevices.Add(device);
            }
        }

        public async Task ConnectAsync()
        {
            var option = SelectedTransportOption;
            if (option == null)
            {
                StatusMessage = "اختر وضع الاتصال أولاً";
                return;
            }

            if (!option.IsImplemented)
            {
                StatusMessage = $"وضع {option.DisplayName} غير جاهز بعد";
                return;
            }

            if (string.Equals(option.Key, "wifiDirect", StringComparison.OrdinalIgnoreCase))
            {
                await ConnectWifiDirectOneClickAsync();
                return;
            }

            var target = SelectedDiscoveredDevice?.DeviceId;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = NormalizeTargetEndpoint(option.Key, TargetEndpoint);
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                if (string.Equals(option.Key, "usb", StringComparison.OrdinalIgnoreCase))
                {
                    target = string.Empty;
                }
                else
                {
                    StatusMessage = "ابحث عن الهاتف أو أدخل عنوانه بالشكل IP:Port";
                    return;
                }
            }

            var device = new Device
            {
                DeviceId = target,
                DeviceName = SelectedDiscoveredDevice?.DeviceName
                    ?? (string.Equals(option.Key, "usb", StringComparison.OrdinalIgnoreCase)
                        ? "هاتف Android عبر USB"
                        : $"هاتف Android ({target})"),
                OsVersion = "Android",
                AppVersion = "Unknown",
                TransportType = option.Key,
                ProxyEndpoint = SelectedDiscoveredDevice?.ProxyEndpoint ?? ProxyEndpoint,
                GatewayEndpoint = SelectedDiscoveredDevice?.GatewayEndpoint ?? GatewayEndpoint
            };

            ProxyEndpoint = device.ProxyEndpoint;
            await ConnectToDeviceAsync(device, option.Key);
        }

        public async Task DetectWifiNetworkAsync(
            bool updateStatus = true,
            bool requestActiveScan = true,
            CancellationToken cancellationToken = default)
        {
            if (IsDetectingWifiNetworks)
            {
                if (!requestActiveScan || _activeWifiNetworkScanRequested)
                {
                    return;
                }

                // An explicit Refresh/Search supersedes a passive startup/transport enumeration.
                CancelWifiNetworkScan();
                for (var attempt = 0; attempt < 20 && IsDetectingWifiNetworks; attempt++)
                {
                    await Task.Delay(25, cancellationToken);
                }

                if (IsDetectingWifiNetworks)
                {
                    return;
                }
            }

            var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _wifiNetworkScanCancellation = scanCancellation;
            _activeWifiNetworkScanRequested = requestActiveScan;
            try
            {
                IsDetectingWifiNetworks = true;
                if (updateStatus)
                {
                    StatusMessage = "جاري كشف شبكات Wi-Fi Direct القريبة...";
                }

                Logger.Info(
                    Tag,
                    requestActiveScan
                        ? "[NETRELAY-WFD] Explicit active scan started"
                        : "[NETRELAY-WFD] Cached WLAN enumeration started");
                var expectedSsid = string.IsNullOrWhiteSpace(WifiDirectNetworkName)
                    ? null
                    : WifiDirectNetworkName;
                var priorSelection = string.IsNullOrWhiteSpace(SelectedWifiDirectNetworkName)
                    ? WifiDirectNetworkName
                    : SelectedWifiDirectNetworkName;
                var networks = await _wifiNetworkScanService.GetVisibleNetworkNamesAsync(
                    expectedSsid,
                    scanCancellation.Token,
                    requestActiveScan);
                var relevantNetworks = networks
                    .Where(name =>
                        name.StartsWith("DIRECT-", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("NetRelay", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(name => name.Contains("NetRelay", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                WifiDirectNetworkNames.Clear();
                foreach (var network in relevantNetworks)
                {
                    WifiDirectNetworkNames.Add(network);
                }

                var bestNetwork = _wifiNetworkScanService.SelectBestNetRelayNetwork(
                    relevantNetworks,
                    priorSelection);
                if (!string.IsNullOrWhiteSpace(bestNetwork))
                {
                    SelectedWifiDirectNetworkName = bestNetwork;
                    WifiDirectNetworkName = bestNetwork;
                    if (string.IsNullOrWhiteSpace(TargetEndpoint))
                    {
                        TargetEndpoint = DefaultWifiDirectEndpoint;
                    }
                    WifiDirectHasSavedProfile = await _wifiNetworkScanService.HasSavedProfileAsync(bestNetwork);
                    WifiDirectProfileStatus = WifiDirectHasSavedProfile == true
                        ? "الشبكة محفوظة ✓"
                        : "أدخل كلمة المرور لأول اتصال فقط";
                    if (updateStatus)
                    {
                        StatusMessage = $"تم كشف شبكة Wi-Fi Direct: {bestNetwork}";
                    }
                    return;
                }

                SelectedWifiDirectNetworkName = null;
                WifiDirectNetworkName = string.Empty;
                WifiDirectHasSavedProfile = null;
                WifiDirectProfileStatus = "سيُستخدم WLAN Profile المحفوظ تلقائيًا إن وُجد";

                if (updateStatus)
                {
                    StatusMessage = "لم أجد شبكة NetRelay/DIRECT. شغل Wi-Fi Direct في تطبيق الهاتف ثم أعد الكشف.";
                }
            }
            catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
            {
                Logger.Info(Tag, "[NETRELAY-WFD] WLAN discovery canceled");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Wi-Fi network detection error: {ex.Message}", ex);
                if (updateStatus)
                {
                    StatusMessage = $"فشل كشف شبكة Wi-Fi: {ex.Message}";
                }
            }
            finally
            {
                if (ReferenceEquals(_wifiNetworkScanCancellation, scanCancellation))
                {
                    _wifiNetworkScanCancellation = null;
                    _activeWifiNetworkScanRequested = false;
                    IsDetectingWifiNetworks = false;
                }
                scanCancellation.Dispose();
                RefreshDiagnostics();
            }
        }

        private void CancelWifiNetworkScan()
        {
            try
            {
                _wifiNetworkScanCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The owning scan already completed and disposed its linked token source.
            }
        }

        public async Task ConnectWifiNetworkAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(WifiDirectNetworkName))
                {
                    await DetectWifiNetworkAsync(updateStatus: true, requestActiveScan: false);
                }

                if (string.IsNullOrWhiteSpace(WifiDirectNetworkName))
                {
                    StatusMessage = "لم يتم كشف اسم شبكة Wi-Fi Direct بعد";
                    return;
                }

                IsDetectingWifiNetworks = true;
                StatusMessage = $"جاري اتصال Windows بالشبكة {WifiDirectNetworkName}...";
                var result = await _wifiNetworkScanService.EnsureConnectedAsync(
                    WifiDirectNetworkName,
                    WifiDirectPassphrase);

                WifiDirectProfileStatus = result.SavedProfileFound
                    ? "الشبكة محفوظة ✓"
                    : result.PasswordRequired
                        ? "كلمة المرور مطلوبة لأول اتصال"
                        : "لا يوجد Profile محفوظ";
                WifiDirectHasSavedProfile = result.SavedProfileFound;
                WifiDirectWindowsIp = result.Ipv4Address ?? string.Empty;
                if (result.Connected)
                {
                    var androidHost = GetWifiDirectHost();
                    var endpointReachable = await _wifiNetworkScanService.TestTcpEndpointAsync(
                        androidHost,
                        NetRelayDataPlaneService.AndroidGatewayPort);
                    StatusMessage = endpointReachable
                        ? "Wi-Fi Direct connected and Android P2P endpoint:8991 is reachable. Ready for AEL2 test."
                        : "Windows connected to the SSID, but Android P2P endpoint:8991 is unreachable. AEL2/Wintun will not start.";
                    return;
                }
                StatusMessage = result.ErrorMessage
                    ?? "فشل اتصال Windows بالشبكة. تأكد من كلمة السر وأن الشبكة ظاهرة.";
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Wi-Fi connect error: {ex.Message}", ex);
                StatusMessage = $"فشل اتصال Wi-Fi: {ex.Message}";
            }
            finally
            {
                IsDetectingWifiNetworks = false;
                RefreshDiagnostics();
            }
        }

        private async Task ConnectWifiDirectOneClickAsync()
        {
            if (IsConnecting || IsDetectingWifiNetworks)
            {
                return;
            }

            Logger.Info(Tag, "[NETRELAY-WFD] One-click connect started");
            try
            {
                IsConnecting = true;
                IsDetectingWifiNetworks = true;
                StatusMessage = "جاري تجهيز اتصال Wi-Fi Direct تلقائيًا...";

                if (string.IsNullOrWhiteSpace(WifiDirectNetworkName))
                {
                    IsDetectingWifiNetworks = false;
                    await DetectWifiNetworkAsync(updateStatus: true, requestActiveScan: false);
                    IsDetectingWifiNetworks = true;
                }

                if (string.IsNullOrWhiteSpace(WifiDirectNetworkName))
                {
                    StatusMessage = "لم يتم كشف اسم شبكة Wi-Fi Direct بعد";
                    return;
                }

                Logger.Info(Tag, $"[NETRELAY-WFD] Expected SSID={WifiDirectNetworkName}");
                var wlanResult = await _wifiNetworkScanService.EnsureConnectedAsync(
                    WifiDirectNetworkName,
                    WifiDirectPassphrase);

                WifiDirectProfileStatus = wlanResult.SavedProfileFound
                    ? "الشبكة محفوظة ✓"
                    : wlanResult.PasswordRequired
                        ? "أدخل كلمة المرور مرة واحدة"
                        : "لا يوجد Profile محفوظ";
                WifiDirectHasSavedProfile = wlanResult.SavedProfileFound;
                WifiDirectWindowsIp = wlanResult.Ipv4Address ?? string.Empty;

                if (!wlanResult.Connected)
                {
                    UiState = UiState.Error;
                    StatusMessage = wlanResult.ErrorMessage ?? "فشل اتصال Windows بشبكة Wi-Fi Direct";
                    return;
                }

                Logger.Info(Tag, "[NETRELAY-WFD] WLAN connected");
                if (!string.IsNullOrWhiteSpace(wlanResult.Ipv4Address))
                {
                    Logger.Info(Tag, $"[NETRELAY-WFD] IPv4 ready={wlanResult.Ipv4Address}");
                }

                var androidHost = GetWifiDirectHost();
                TargetEndpoint = $"{androidHost}:8988";
                StatusMessage = $"تم اتصال الشبكة؛ جاري انتظار بوابة الهاتف {androidHost}:8991...";
                if (!await _wifiNetworkScanService.TestTcpEndpointAsync(
                        androidHost,
                        NetRelayDataPlaneService.AndroidGatewayPort))
                {
                    UiState = UiState.Error;
                    StatusMessage = $"Windows متصل بالشبكة، لكن بوابة Android {androidHost}:8991 غير متاحة";
                    return;
                }

                Logger.Info(Tag, "[NETRELAY-WFD] Android endpoint reachable");
                var device = new Device
                {
                    DeviceId = TargetEndpoint,
                    DeviceName = $"هاتف Android ({TargetEndpoint})",
                    OsVersion = "Android",
                    AppVersion = "Unknown",
                    TransportType = "wifiDirect",
                    ProxyEndpoint = ProxyEndpoint,
                    GatewayEndpoint = GatewayEndpoint
                };

                IsConnecting = false;
                await ConnectToDeviceAsync(device, "wifiDirect");
                if (UiState == UiState.Connected && IsAdapterModeRunning)
                {
                    Logger.Info(Tag, "[NETRELAY-WFD] INTERNET DATA PLANE READY");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Wi-Fi Direct one-click error: {ex.Message}", ex);
                UiState = UiState.Error;
                StatusMessage = $"فشل اتصال Wi-Fi Direct: {ex.Message}";
            }
            finally
            {
                IsDetectingWifiNetworks = false;
                IsConnecting = false;
                RefreshDiagnostics();
            }
        }

        private string GetWifiDirectHost()
        {
            if (TryGetWifiDirectHost(_wifiDirectEndpoint, out var host))
            {
                Logger.Info(Tag, $"[NETRELAY-WFD] Discovered P2P host={host}");
                Logger.Info(Tag, $"[NETRELAY-WFD] Using Wi-Fi Direct host={host}");
                return host;
            }

            TryGetWifiDirectHost(DefaultWifiDirectEndpoint, out host);
            _wifiDirectEndpoint = DefaultWifiDirectEndpoint;
            Logger.Info(Tag, $"[NETRELAY-WFD] Using Wi-Fi Direct host={host}");
            return host;
        }

        internal static bool TryGetWifiDirectHost(string? endpoint, out string host)
        {
            host = string.Empty;
            var value = endpoint?.Trim() ?? string.Empty;
            if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host) &&
                IPAddress.TryParse(uri.Host, out var uriAddress) &&
                IsValidWifiDirectHost(uriAddress))
            {
                host = uri.Host;
                return true;
            }

            if (value.StartsWith("[", StringComparison.Ordinal) && value.Contains(']'))
            {
                var bracketHost = value[1..value.IndexOf(']')];
                if (IPAddress.TryParse(bracketHost, out var bracketAddress) &&
                    IsValidWifiDirectHost(bracketAddress))
                {
                    host = bracketHost;
                    return true;
                }
            }

            var separator = value.LastIndexOf(':');
            var candidate = separator > 0 ? value[..separator] : value;
            if (IPAddress.TryParse(candidate, out var address) && IsValidWifiDirectHost(address))
            {
                host = candidate;
                return true;
            }

            return false;
        }

        private static bool IsValidWifiDirectHost(IPAddress address)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            return !(bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 123);
        }

        private void RememberEndpointForTransport(string? transportType, string endpoint)
        {
            if (string.Equals(transportType, "usb", StringComparison.OrdinalIgnoreCase))
            {
                _usbEndpoint = endpoint;
            }
            else if (string.Equals(transportType, "wifiDirect", StringComparison.OrdinalIgnoreCase))
            {
                _wifiDirectEndpoint = TryGetWifiDirectHost(endpoint, out _)
                    ? endpoint
                    : DefaultWifiDirectEndpoint;
            }
        }

        private void ApplyEndpointForTransport(string transportType)
        {
            var endpoint = string.Equals(transportType, "usb", StringComparison.OrdinalIgnoreCase)
                ? _usbEndpoint
                : _wifiDirectEndpoint;
            SetProperty(ref _targetEndpoint, endpoint, nameof(TargetEndpoint));
            OnPropertyChanged(nameof(UsbControlEndpoint));
            OnPropertyChanged(nameof(WifiDirectControlEndpoint));
        }

        private bool IsWifiDirectSelectedOrActive()
        {
            return string.Equals(CurrentDevice?.TransportType, "wifiDirect", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SelectedTransportOption?.Key, "wifiDirect", StringComparison.OrdinalIgnoreCase);
        }

        public async Task PreparePhoneAsync()
        {
            try
            {
                IsConnecting = true;
                StatusMessage = "جاري تثبيت أو تشغيل تطبيق الهاتف عبر USB...";
                RefreshDiagnostics();

                var requestedSerial = SelectedDiscoveredDevice?.DeviceId;
                if (string.IsNullOrWhiteSpace(requestedSerial) &&
                    string.Equals(SelectedTransportOption?.Key, "usb", StringComparison.OrdinalIgnoreCase))
                {
                    requestedSerial = TargetEndpoint;
                }

                var preparedPhone = await _adbBridgeService.InstallOrUpdateNetRelayAsync(
                    requestedSerial,
                    "usb");
                var preparedBridge = await _adbBridgeService.PrepareNetRelayUsbAsync(preparedPhone.Serial);

                TargetEndpoint = preparedBridge.Serial;
                ProxyEndpoint = preparedBridge.ProxyEndpoint;
                GatewayEndpoint = preparedBridge.GatewayEndpoint;
                StatusMessage = $"تم تجهيز تطبيق الهاتف على {preparedBridge.DeviceName}";

                var usbOption = TransportOptions.FirstOrDefault(option =>
                    string.Equals(option.Key, "usb", StringComparison.OrdinalIgnoreCase));
                if (usbOption != null)
                {
                    SelectedTransportOption = usbOption;
                }

                await DiscoverUsbDevicesAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Phone preparation error: {ex.Message}", ex);
                UiState = UiState.Error;
                StatusMessage = $"فشل تجهيز الهاتف: {ex.Message}";
            }
            finally
            {
                IsConnecting = false;
                RefreshDiagnostics();
            }
        }

        public async Task<AdbBridgeService.AdbPreparedPhone> PreparePhoneForProductionAsync(
            string? requestedSerial,
            bool allowSignatureReset,
            Action<string>? progress = null)
        {
            try
            {
                IsConnecting = true;
                StatusMessage = "Checking the Android companion...";
                RefreshDiagnostics();

                requestedSerial = string.IsNullOrWhiteSpace(requestedSerial)
                    ? SelectedDiscoveredDevice?.DeviceId
                    : requestedSerial;
                var preparedPhone = await _adbBridgeService.InstallOrUpdateNetRelayAsync(
                    requestedSerial,
                    "usb",
                    allowSignatureReset,
                    progress);

                StatusMessage = preparedPhone.Outcome switch
                {
                    "newerInstalled" => "A newer Net Relay Android version is already installed and was opened.",
                    "installed" => "Net Relay Android was installed and opened.",
                    "reinstalled" => "Net Relay Android was reinstalled and opened.",
                    _ => "Net Relay Android was updated and opened."
                };
                return preparedPhone;
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Production Android preparation error: {ex.Message}", ex);
                UiState = UiState.Error;
                StatusMessage = "Android installation or launch failed.";
                throw;
            }
            finally
            {
                IsConnecting = false;
                RefreshDiagnostics();
            }
        }

        public async Task ShutdownAsync(TimeSpan timeout)
        {
            try
            {
                CancelWifiNetworkScan();
                StopNetworkMetrics();
                var cleanupTask = Task.Run(ShutdownServicesAsync);
                var timeoutTask = Task.Delay(timeout);
                var completedTask = await Task.WhenAny(cleanupTask, timeoutTask);

                if (completedTask == cleanupTask)
                {
                    await cleanupTask;
                    return;
                }

                Logger.Warn(Tag, $"Shutdown cleanup timed out after {timeout.TotalSeconds:0.#} seconds");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, "Shutdown cleanup failed", ex);
            }
        }

        public void Shutdown()
        {
            _ = ShutdownAsync(TimeSpan.FromSeconds(3));
        }

        private async Task ShutdownServicesAsync()
        {
            MarkManualDisconnect();
            StopHeartbeatMonitor();
            DisposeTransportSubscriptions();

            try
            {
                await _sessionManager.CloseSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Shutdown session cleanup failed: {ex.Message}", ex);
            }

            try
            {
                await StopAdapterModeForShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Shutdown adapter cleanup failed: {ex.Message}", ex);
            }

            try
            {
                await _transportManager.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Shutdown transport cleanup failed: {ex.Message}", ex);
            }

            try
            {
                await RestoreWindowsProxyForShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Shutdown proxy cleanup failed: {ex.Message}", ex);
            }
        }

        private void SubscribeToProtocolMessages()
        {
            _protocolSubscription = _protocolHandler.IncomingMessages.Subscribe(message =>
            {
                StatusMessage = message.Type switch
                {
                    MessageTypes.StatusUpdate => CurrentDevice != null
                        ? $"الاتصال نشط مع {CurrentDevice.DeviceName}"
                        : "تم تحديث حالة الهاتف",
                    MessageTypes.HeartbeatAck => "تم استلام نبضة تأكيد من الهاتف",
                    MessageTypes.DisconnectAck => "الهاتف أكد قطع الاتصال",
                    _ => $"تم استلام {message.Type}"
                };
                if (string.Equals(message.Type, MessageTypes.HeartbeatAck, StringComparison.OrdinalIgnoreCase))
                {
                    _missedHeartbeats = 0;
                }

                if (string.Equals(message.Type, MessageTypes.StatusUpdate, StringComparison.OrdinalIgnoreCase))
                {
                    var endpoint = GetPayloadValue(message, "endpoint")
                        ?? GetPayloadValue(message, "listenerEndpoint");
                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        TargetEndpoint = endpoint;
                    }

                    var proxyEndpoint = GetPayloadValue(message, "proxyEndpoint");
                    if (!string.IsNullOrWhiteSpace(proxyEndpoint))
                    {
                        ProxyEndpoint = proxyEndpoint;
                        if (CurrentDevice != null)
                        {
                            CurrentDevice.ProxyEndpoint = proxyEndpoint;
                        }
                        if (_lastConnectionDevice != null)
                        {
                            _lastConnectionDevice.ProxyEndpoint = proxyEndpoint;
                        }
                    }

                    var gatewayEndpoint = GetPayloadValue(message, "gatewayEndpoint")
                        ?? GetPayloadValue(message, "socksEndpoint");
                    if (!string.IsNullOrWhiteSpace(gatewayEndpoint))
                    {
                        GatewayEndpoint = gatewayEndpoint;
                        if (CurrentDevice != null)
                        {
                            CurrentDevice.GatewayEndpoint = gatewayEndpoint;
                        }
                        if (_lastConnectionDevice != null)
                        {
                            _lastConnectionDevice.GatewayEndpoint = gatewayEndpoint;
                        }
                    }

                    var phoneProxyStatus = GetPayloadValue(message, "proxyStatus");
                    if (!string.IsNullOrWhiteSpace(phoneProxyStatus))
                    {
                        ProxyStatus = phoneProxyStatus;
                    }

                    var wifiNetworkName = GetPayloadValue(message, "wifiNetworkName");
                    if (!string.IsNullOrWhiteSpace(wifiNetworkName))
                    {
                        WifiDirectNetworkName = wifiNetworkName;
                    }

                    var wifiPassphrase = GetPayloadValue(message, "wifiPassphrase");
                    if (!string.IsNullOrWhiteSpace(wifiPassphrase))
                    {
                        WifiDirectPassphrase = wifiPassphrase;
                    }

                    if (UiState == UiState.Connected)
                    {
                        // A later STATUS_UPDATE may announce a healthier endpoint after reconnect,
                        // so allow the adapter layer to upgrade or rebind automatically.
                        _ = StartAdapterModeForCurrentProxyAsync(updateStatusOnly: true);
                    }
                }

                Logger.Info(Tag, $"Protocol message received: {message.Type}");
                RefreshDiagnostics();
            });
        }

        private void SubscribeToActiveTransport()
        {
            DisposeTransportSubscriptions();

            var incomingObservable = _transportManager.ObserveIncoming();
            if (incomingObservable != null)
            {
                _incomingSubscription = incomingObservable.Subscribe(data =>
                {
                    _ = _protocolHandler.ProcessIncomingAsync(data);
                });
            }

            var stateObservable = _transportManager.ObserveState();
            if (stateObservable != null)
            {
                _stateSubscription = stateObservable.Subscribe(state =>
                {
                    Logger.Info(Tag, $"Transport state changed to {state}");
                    HandleTransportStateForRecovery(state);
                    RefreshDiagnostics();
                });
            }
        }

        private async Task SendHandshakeAsync(string sessionId)
        {
            var handshakeBytes = _protocolHandler.SendHandshake(
                Environment.MachineName,
                $"windows-{Environment.MachineName}".ToLowerInvariant(),
                Environment.OSVersion.VersionString,
                typeof(ConnectionViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                new[] { "wifiDirect", "usb" },
                sessionId);

            if (await _transportManager.SendAsync(handshakeBytes))
            {
                return;
            }

            Logger.Warn(Tag, "Connected, but handshake send failed");
            StatusMessage = "تم الاتصال لكن فشلت المصافحة";
        }

        private void StartHeartbeatMonitor()
        {
            StopHeartbeatMonitor();
            _missedHeartbeats = 0;
            _heartbeatCancellation = new CancellationTokenSource();
            _ = HeartbeatLoopAsync(_heartbeatCancellation.Token);
        }

        private void StopHeartbeatMonitor()
        {
            _heartbeatCancellation?.Cancel();
            _heartbeatCancellation?.Dispose();
            _heartbeatCancellation = null;
            _missedHeartbeats = 0;
        }

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

                    if (!_sessionManager.IsSessionActive() || !_transportManager.IsConnected())
                    {
                        continue;
                    }

                    var session = _sessionManager.GetSession();
                    if (session == null)
                    {
                        continue;
                    }

                    var heartbeatBytes = _protocolHandler.SendHeartbeat(sessionId: session.SessionId);
                    if (!await _transportManager.SendAsync(heartbeatBytes))
                    {
                        _missedHeartbeats++;
                    }
                    else
                    {
                        _missedHeartbeats++;
                        Logger.Debug(Tag, $"Heartbeat sent, missed count: {_missedHeartbeats}");
                    }

                    if (_missedHeartbeats > 3)
                    {
                        Logger.Warn(Tag, "Heartbeat timeout reached");
                        _sessionManager.HandleReconnect();
                        StatusMessage = "انقطع رد الهاتف، جاري إصلاح الاتصال تلقائيا";
                        RefreshDiagnostics();
                        ScheduleConnectionRecovery("انتهاء مهلة heartbeat");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Debug(Tag, "Heartbeat monitor stopped");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Heartbeat monitor error: {ex.Message}", ex);
            }
        }

        private void RefreshDiagnostics()
        {
            LogOutput = Logger.Instance.ExportLogs();
            _connectCommand.RaiseCanExecuteChanged();
            _disconnectCommand.RaiseCanExecuteChanged();
            _discoverCommand.RaiseCanExecuteChanged();
            _preparePhoneCommand.RaiseCanExecuteChanged();
            _detectWifiNetworkCommand.RaiseCanExecuteChanged();
            _connectWifiNetworkCommand.RaiseCanExecuteChanged();
            RefreshAdapterModeCommandStates();
        }

        private void ClearLogs()
        {
            Logger.Instance.ClearHistory();
            RefreshDiagnostics();
        }

        private void DisposeTransportSubscriptions()
        {
            _incomingSubscription?.Dispose();
            _stateSubscription?.Dispose();
            _incomingSubscription = null;
            _stateSubscription = null;
        }

        private static string? GetPayloadValue(ProtocolMessage message, string key)
        {
            return message.Payload.TryGetValue(key, out var value) ? value : null;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            OnDisplayDependenciesChanged(propertyName);
            return true;
        }
    }
}
