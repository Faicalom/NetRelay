using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NetRelay.Services;
using NetRelay.Utils;

namespace NetRelay.ViewModels
{
    public partial class ConnectionViewModel
    {
        private readonly NetworkAdapterModeService _networkAdapterModeService = new();
        private readonly NetRelayDataPlaneService _dataPlaneService = new();
        private readonly SemaphoreSlim _adapterModeGate = new(1, 1);
        private RelayCommand? _startAdapterModeCommand;
        private RelayCommand? _stopAdapterModeCommand;
        private CancellationTokenSource? _adapterWatchdogCancellation;
        private string _adapterModeStatus = "غير مفعل";
        private string _activeAdapterEndpoint = string.Empty;
        private bool _isAdapterModeRunning;
        private bool _adapterModeDesired;
        private int _adapterRecoveryCount;

        public ICommand StartAdapterModeCommand => _startAdapterModeCommand
            ?? throw new InvalidOperationException("Adapter commands are not initialized.");

        public ICommand StopAdapterModeCommand => _stopAdapterModeCommand
            ?? throw new InvalidOperationException("Adapter commands are not initialized.");

        public string AdapterModeStatus
        {
            get => _adapterModeStatus;
            set => SetProperty(ref _adapterModeStatus, value);
        }

        public bool IsAdapterModeRunning
        {
            get => _isAdapterModeRunning;
            set
            {
                if (SetProperty(ref _isAdapterModeRunning, value))
                {
                    RefreshAdapterModeCommandStates();
                }
            }
        }

        private void InitializeAdapterMode()
        {
            _startAdapterModeCommand = new RelayCommand(
                () => _ = StartAdapterModeAsync(),
                () => !IsAdapterModeRunning && !IsConnecting);
            _stopAdapterModeCommand = new RelayCommand(
                () => _ = StopAdapterModeAsync(),
                () => IsAdapterModeRunning);
        }

        private async Task StartAdapterModeAsync()
        {
            await StartAdapterModeForCurrentProxyAsync(updateStatusOnly: false);
        }

        private async Task<bool> StartAdapterModeForCurrentProxyAsync(bool updateStatusOnly)
        {
            await _adapterModeGate.WaitAsync();
            try
            {
                var endpoint = await PrepareBestDataPlaneEndpointAsync();
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    AdapterModeStatus = "لا توجد بوابة بيانات متاحة من الهاتف بعد";
                    if (!updateStatusOnly)
                    {
                        ProxyStatus = AdapterModeStatus;
                    }
                    return false;
                }

                await EnsureEndpointReachableAsync(endpoint);
                var normalizedEndpoint = NormalizeEndpoint(endpoint);

                if (IsAdapterModeRunning && _networkAdapterModeService.IsRunning)
                {
                    if (string.Equals(
                        _activeAdapterEndpoint,
                        normalizedEndpoint,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        AdapterModeStatus = "وضع المحول يعمل بالفعل";
                        return true;
                    }

                    // The phone may advertise the native SOCKS5 gateway a moment after
                    // the control connection becomes active. Switch from compatibility
                    // HTTP mode to the native data plane without requiring user action.
                    AdapterModeStatus = "تم العثور على مسار بيانات أفضل، جاري التبديل...";
                    await _networkAdapterModeService.StopAsync();
                    IsAdapterModeRunning = false;
                }

                AdapterModeStatus = "جاري تشغيل بوابة الشبكة...";
                RefreshDiagnostics();

                var result = await _networkAdapterModeService.StartAsync(
                    endpoint,
                    _dataPlaneService.TransportTarget);
                _activeAdapterEndpoint = normalizedEndpoint;
                _adapterModeDesired = true;
                _adapterRecoveryCount = 0;
                IsAdapterModeRunning = true;

                var modeText = result.SupportsUdp
                    ? "بوابة أصلية TCP/UDP"
                    : "وضع توافق HTTP/TCP";
                AdapterModeStatus =
                    $"{modeText} عبر {result.AdapterName} ({result.AdapterAddress}) — DNS: {result.DnsEndpoint}";
                ProxyStatus = AdapterModeStatus;
                StartAdapterWatchdog();
                return true;
            }
            catch (Exception ex)
            {
                IsAdapterModeRunning = false;
                _activeAdapterEndpoint = string.Empty;
                AdapterModeStatus = $"فشل وضع المحول: {ex.Message}";
                ProxyStatus = updateStatusOnly
                    ? $"{AdapterModeStatus}. لم يتم تغيير وكيل Windows."
                    : AdapterModeStatus;
                Logger.Error(Tag, $"Adapter mode failed: {ex.Message}", ex);
                return false;
            }
            finally
            {
                RefreshDiagnostics();
                _adapterModeGate.Release();
            }
        }

        private async Task StopAdapterModeAsync()
        {
            await StopAdapterModeInternalAsync(updateProxyStatus: true);
        }

        private async Task StopAdapterModeInternalAsync(bool updateProxyStatus)
        {
            _adapterModeDesired = false;
            StopAdapterWatchdog();
            await _adapterModeGate.WaitAsync();
            try
            {
                await _networkAdapterModeService.StopAsync();
                await _dataPlaneService.StopAsync();
                _activeAdapterEndpoint = string.Empty;
                IsAdapterModeRunning = false;
                AdapterModeStatus = "تم إيقاف وضع المحول";
                if (updateProxyStatus)
                {
                    ProxyStatus = AdapterModeStatus;
                }
            }
            catch (Exception ex)
            {
                AdapterModeStatus = $"تعذر إيقاف وضع المحول: {ex.Message}";
                Logger.Error(Tag, $"Adapter mode stop failed: {ex.Message}", ex);
            }
            finally
            {
                RefreshDiagnostics();
                _adapterModeGate.Release();
            }
        }

        private async Task StopAdapterModeForShutdownAsync()
        {
            _adapterModeDesired = false;
            StopAdapterWatchdog();

            if (!await _adapterModeGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            {
                Logger.Warn(Tag, "Skipped adapter shutdown cleanup because adapter gate is busy");
                return;
            }

            try
            {
                await _networkAdapterModeService.StopAsync().ConfigureAwait(false);
                await _dataPlaneService.StopAsync().ConfigureAwait(false);
                _activeAdapterEndpoint = string.Empty;
            }
            finally
            {
                _adapterModeGate.Release();
            }
        }

        private void RefreshAdapterModeCommandStates()
        {
            _startAdapterModeCommand?.RaiseCanExecuteChanged();
            _stopAdapterModeCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Builds the native NetRelay v2 data plane.  Unlike the earlier direct-SOCKS
        /// experiment, SOCKS5 now exists only on the PC (127.0.0.1:8990).  TCP and UDP
        /// are multiplexed over one reliable AEL2 byte stream to Android.  This is the
        /// important PdaNet-style split: virtual adapter -> user-mode gateway -> phone
        /// socket engine.
        /// </summary>
        private async Task<string> PrepareBestDataPlaneEndpointAsync()
        {
            var transportType = CurrentDevice?.TransportType
                ?? SelectedTransportOption?.Key
                ?? string.Empty;

            // Capture the transport endpoint once for this data-plane session. UI changes
            // after HELLO/HELLO_ACK cannot replace the active session's target.
            var targetSnapshot = CurrentDevice?.DeviceId
                ?? SelectedDiscoveredDevice?.DeviceId
                ?? TargetEndpoint;

            if (!string.IsNullOrWhiteSpace(transportType))
            {
                try
                {
                    AdapterModeStatus = $"جاري تجهيز بوابة NetRelay v2 عبر {transportType}...";
                    if (string.Equals(transportType, "usb", StringComparison.OrdinalIgnoreCase))
                        Logger.Info(Tag, "[NETRELAY-USB] Calling StartForTransportAsync usb");
                    var localSocks = await _dataPlaneService.StartForTransportAsync(
                        transportType,
                        targetSnapshot ?? string.Empty,
                        _adbBridgeService);

                    GatewayEndpoint = localSocks;
                    if (CurrentDevice != null)
                    {
                        CurrentDevice.GatewayEndpoint = localSocks;
                    }
                    Logger.Info(Tag, $"Using NetRelay v2 framed gateway through {transportType}");
                    return localSocks;
                }
                catch (Exception ex)
                {
                    if (string.Equals(transportType, "usb", StringComparison.OrdinalIgnoreCase))
                        Logger.Error(Tag, $"[NETRELAY-USB] FAILED stage=PrepareBestDataPlaneEndpointAsync type={ex.GetType().FullName} message={ex.Message}\\n{ex.StackTrace}", ex);
                    Logger.Warn(Tag, $"NetRelay v2 data plane unavailable: {ex.Message}");
                    await _dataPlaneService.StopAsync();
                    GatewayEndpoint = string.Empty;
                    if (CurrentDevice != null)
                    {
                        CurrentDevice.GatewayEndpoint = string.Empty;
                    }
                    throw new InvalidOperationException(
                        $"{transportType} AEL2 data plane failed; no legacy fallback exists: {ex.Message}", ex);
                }
            }
            throw new InvalidOperationException("No production AEL2 transport is selected.");
        }

        private async Task EnsureEndpointReachableAsync(string endpoint)
        {
            var parsed = NetworkGatewayEndpoint.Parse(endpoint);
            if (!IsLoopback(parsed.Uri.Host))
            {
                return;
            }

            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                if (await IsTcpPortOpenAsync(parsed.Uri.Host, parsed.Uri.Port, TimeSpan.FromMilliseconds(700)))
                {
                    return;
                }

                await Task.Delay(300);
            }

            throw new InvalidOperationException(
                $"بوابة الهاتف المحلية غير متاحة على {parsed.Uri.Host}:{parsed.Uri.Port}.");
        }

        private static async Task<bool> IsTcpPortOpenAsync(
            string host,
            int port,
            TimeSpan timeout)
        {
            try
            {
                using var tcpClient = new TcpClient();
                using var cancellationTokenSource = new CancellationTokenSource(timeout);
                await tcpClient.ConnectAsync(host, port, cancellationTokenSource.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StartAdapterWatchdog()
        {
            StopAdapterWatchdog();
            _adapterWatchdogCancellation = new CancellationTokenSource();
            _ = AdapterWatchdogLoopAsync(_adapterWatchdogCancellation.Token);
        }

        private void StopAdapterWatchdog()
        {
            _adapterWatchdogCancellation?.Cancel();
            _adapterWatchdogCancellation?.Dispose();
            _adapterWatchdogCancellation = null;
        }

        private async Task AdapterWatchdogLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);

                    if (!_adapterModeDesired)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(_activeAdapterEndpoint))
                    {
                        var endpoint = NetworkGatewayEndpoint.Parse(_activeAdapterEndpoint);
                        if (IsLoopback(endpoint.Uri.Host) &&
                            !await IsTcpPortOpenAsync(
                                endpoint.Uri.Host,
                                endpoint.Uri.Port,
                                TimeSpan.FromMilliseconds(900)))
                        {
                            await RecoverAdapterModeAsync(
                                $"انقطع مسار الهاتف {endpoint.Uri.Host}:{endpoint.Uri.Port}",
                                cancellationToken);
                            continue;
                        }
                    }

                    if (IsLocalNativeUsbGateway(_activeAdapterEndpoint) &&
                        !_dataPlaneService.IsRunning)
                    {
                        await RecoverAdapterModeAsync("انقطع محرك NetRelay v2 عن الهاتف", cancellationToken);
                        continue;
                    }

                    if (!_networkAdapterModeService.IsRunning)
                    {
                        await RecoverAdapterModeAsync("توقف محرك المحول", cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Debug(Tag, "Adapter watchdog stopped");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Adapter watchdog error: {ex.Message}", ex);
            }
        }

        private async Task RecoverAdapterModeAsync(string reason, CancellationToken cancellationToken)
        {
            if (!await _adapterModeGate.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                if (!_adapterModeDesired)
                {
                    return;
                }

                AdapterModeStatus = $"إصلاح تلقائي: {reason}...";
                RefreshDiagnostics();

                var endpoint = await PrepareBestDataPlaneEndpointAsync();
                await EnsureEndpointReachableAsync(endpoint);
                var result = await _networkAdapterModeService.StartAsync(
                    endpoint,
                    _dataPlaneService.TransportTarget,
                    cancellationToken);

                _activeAdapterEndpoint = NormalizeEndpoint(endpoint);
                _adapterRecoveryCount++;
                IsAdapterModeRunning = true;
                AdapterModeStatus =
                    $"تم إصلاح الاتصال تلقائيا ({_adapterRecoveryCount}) — {result.GatewayMode}";
                ProxyStatus = AdapterModeStatus;
                Logger.Warn(Tag, $"Adapter mode recovered: {reason}");
            }
            catch (Exception ex)
            {
                IsAdapterModeRunning = false;
                _activeAdapterEndpoint = string.Empty;
                AdapterModeStatus = $"فشل الإصلاح التلقائي: {ex.Message}";
                ProxyStatus = AdapterModeStatus;
                Logger.Error(Tag, $"Adapter recovery failed: {ex.Message}", ex);
            }
            finally
            {
                RefreshDiagnostics();
                _adapterModeGate.Release();
            }
        }

        private static string NormalizeEndpoint(string endpoint)
        {
            return NetworkGatewayEndpoint.Parse(endpoint).Uri.ToString().TrimEnd('/');
        }

        private static bool IsLoopback(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        }

        private static bool IsLocalNativeUsbGateway(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return false;
            }

            try
            {
                var parsed = NetworkGatewayEndpoint.Parse(endpoint);
                return parsed.Kind == NetworkGatewayKind.Socks5 &&
                    IsLoopback(parsed.Uri.Host) &&
                    parsed.Uri.Port == 8990;
            }
            catch
            {
                return false;
            }
        }
    }
}
