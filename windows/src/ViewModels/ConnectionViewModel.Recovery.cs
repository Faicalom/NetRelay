using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Models;
using NetRelay.Transport;

namespace NetRelay.ViewModels
{
    public partial class ConnectionViewModel
    {
        private readonly SemaphoreSlim _connectionRecoveryGate = new(1, 1);
        private readonly SemaphoreSlim _connectionAttemptGate = new(1, 1);
        private CancellationTokenSource? _connectionRecoveryCancellation;
        private long _connectionAttemptGeneration;
        private Device? _lastConnectionDevice;
        private string? _lastConnectionTransportKey;
        private bool _manualDisconnectRequested;
        private bool _recoveryInProgress;
        private bool _autoReconnectEnabled = true;

        public bool AutoReconnectEnabled
        {
            get => _autoReconnectEnabled;
            set
            {
                if (SetProperty(ref _autoReconnectEnabled, value) && !value)
                {
                    CancelConnectionRecovery();
                }
            }
        }

        private void BeginUserConnectionAttempt()
        {
            _manualDisconnectRequested = false;
            CancelConnectionRecovery();
        }

        private bool TryBeginSerializedConnectionAttempt(out long generation)
        {
            generation = 0;
            if (!_connectionAttemptGate.Wait(0))
                return false;
            generation = Interlocked.Increment(ref _connectionAttemptGeneration);
            Logger.Info(Tag, $"[NETRELAY-CONNECT] BEGIN generation={generation}");
            return true;
        }

        private bool IsCurrentConnectionAttempt(long generation) =>
            generation == Interlocked.Read(ref _connectionAttemptGeneration);

        private void EnsureCurrentConnectionAttempt(long generation)
        {
            if (!IsCurrentConnectionAttempt(generation))
                throw new OperationCanceledException($"Stale connection generation {generation}");
        }

        private void EndSerializedConnectionAttempt(long generation)
        {
            Logger.Info(Tag, $"[NETRELAY-CONNECT] END generation={generation} current={IsCurrentConnectionAttempt(generation)}");
            _connectionAttemptGate.Release();
        }

        private void InvalidateConnectionAttempts(string reason)
        {
            var generation = Interlocked.Increment(ref _connectionAttemptGeneration);
            Logger.Info(Tag, $"[NETRELAY-CONNECT] INVALIDATE generation={generation} reason={reason}");
        }

        private void RememberSuccessfulConnection(Device device, string transportType)
        {
            _lastConnectionDevice = CloneDevice(device);
            _lastConnectionTransportKey = transportType;
        }

        private void MarkManualDisconnect()
        {
            _manualDisconnectRequested = true;
            InvalidateConnectionAttempts("manual disconnect");
            CancelConnectionRecovery();
        }

        private void CancelConnectionRecovery()
        {
            _connectionRecoveryCancellation?.Cancel();
            _connectionRecoveryCancellation?.Dispose();
            _connectionRecoveryCancellation = null;
        }

        private void HandleTransportStateForRecovery(ITransport.TransportState state)
        {
            if (!AutoReconnectEnabled || _manualDisconnectRequested || _recoveryInProgress || IsConnecting)
            {
                return;
            }

            if (state is ITransport.TransportState.Error or ITransport.TransportState.Disconnected)
            {
                ScheduleConnectionRecovery($"تغيرت حالة النقل إلى {state}");
            }
        }

        private void ScheduleConnectionRecovery(string reason)
        {
            if (!AutoReconnectEnabled || _manualDisconnectRequested ||
                _lastConnectionDevice == null ||
                string.IsNullOrWhiteSpace(_lastConnectionTransportKey))
            {
                return;
            }

            _ = RecoverConnectionAsync(reason);
        }

        private async Task RecoverConnectionAsync(string reason)
        {
            if (!await _connectionRecoveryGate.WaitAsync(0))
            {
                return;
            }

            long connectionGeneration = 0;
            var ownsConnectionAttempt = false;
            try
            {
                if (!TryBeginSerializedConnectionAttempt(out connectionGeneration))
                {
                    Logger.Info(Tag, "[NETRELAY-CONNECT] Recovery skipped; another connection attempt is active");
                    return;
                }
                ownsConnectionAttempt = true;
                if (_manualDisconnectRequested ||
                    _lastConnectionDevice == null ||
                    string.IsNullOrWhiteSpace(_lastConnectionTransportKey))
                {
                    return;
                }

                _recoveryInProgress = true;
                IsConnecting = true;
                UiState = UiState.Reconnecting;
                _sessionManager.HandleReconnect();
                StopHeartbeatMonitor();

                CancelConnectionRecovery();
                _connectionRecoveryCancellation = new CancellationTokenSource();
                var cancellationToken = _connectionRecoveryCancellation.Token;

                var device = CloneDevice(_lastConnectionDevice);
                var transportType = _lastConnectionTransportKey;
                Logger.Warn(Tag, $"Connection recovery started: {reason}");

                try
                {
                    await StopAdapterModeInternalAsync(updateProxyStatus: false);
                }
                catch (Exception ex)
                {
                    Logger.Warn(Tag, $"Adapter cleanup before reconnect failed: {ex.Message}");
                }

                var delays = new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4),
                    TimeSpan.FromSeconds(8)
                };

                for (var attempt = 0; attempt < delays.Length; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_manualDisconnectRequested)
                    {
                        return;
                    }

                    if (delays[attempt] > TimeSpan.Zero)
                    {
                        await Task.Delay(delays[attempt], cancellationToken);
                    }

                    StatusMessage =
                        $"إعادة الاتصال تلقائيا ({attempt + 1}/{delays.Length})...";
                    RefreshDiagnostics();

                    try
                    {
                        await _transportManager.DisconnectAsync();
                        EnsureCurrentConnectionAttempt(connectionGeneration);

                        if (!_transportManager.SetActiveTransport(transportType))
                        {
                            throw new InvalidOperationException(
                                $"وضع النقل غير مسجل: {transportType}");
                        }

                        var connected = await _transportManager.ConnectAsync(device.DeviceId);
                        EnsureCurrentConnectionAttempt(connectionGeneration);
                        if (!connected)
                        {
                            throw new InvalidOperationException("فشل فتح قناة النقل");
                        }

                        SubscribeToActiveTransport();
                        CurrentDevice = device;
                        CurrentTransport = TransportOptions
                            .FirstOrDefault(option => option.Key == transportType)?.DisplayName
                            ?? transportType;
                        ProxyEndpoint = device.ProxyEndpoint;
                        GatewayEndpoint = device.GatewayEndpoint;

                        var sessionId = _sessionManager.InitiateSession(device);
                        _sessionManager.StartHandshake();
                        await SendHandshakeAsync(sessionId);
                        EnsureCurrentConnectionAttempt(connectionGeneration);
                        await PrepareAndApplyProxyAsync();
                        EnsureCurrentConnectionAttempt(connectionGeneration);
                        if (!IsAdapterModeRunning || !_dataPlaneService.IsRunning)
                            throw new InvalidOperationException("Recovery data plane validation failed.");
                        _sessionManager.MarkActive();
                        StartHeartbeatMonitor();

                        RememberSuccessfulConnection(device, transportType);
                        UiState = UiState.Connected;
                        StatusMessage =
                            $"تمت استعادة الاتصال تلقائيا مع {device.DeviceName}";
                        Logger.Info(Tag, $"Connection recovered on attempt {attempt + 1}");
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(
                            Tag,
                            $"Recovery attempt {attempt + 1}/{delays.Length} failed: {ex.Message}");
                        try
                        {
                            await StopAdapterModeInternalAsync(updateProxyStatus: false);
                            await _transportManager.DisconnectAsync();
                            await _sessionManager.CloseSessionAsync();
                        }
                        catch (Exception rollbackError)
                        {
                            Logger.Warn(Tag, $"Recovery rollback failed: {rollbackError.Message}");
                        }
                    }
                }

                _sessionManager.MarkError("Automatic reconnection exhausted");
                UiState = UiState.Error;
                StatusMessage = "تعذر استعادة الاتصال تلقائيا بعد عدة محاولات";
            }
            catch (OperationCanceledException)
            {
                Logger.Debug(Tag, "Connection recovery cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Connection recovery failed: {ex.Message}", ex);
                UiState = UiState.Error;
                StatusMessage = $"فشل إصلاح الاتصال: {ex.Message}";
            }
            finally
            {
                _recoveryInProgress = false;
                IsConnecting = false;
                RefreshDiagnostics();
                if (ownsConnectionAttempt)
                    EndSerializedConnectionAttempt(connectionGeneration);
                _connectionRecoveryGate.Release();
            }
        }

        private static Device CloneDevice(Device source)
        {
            return new Device
            {
                DeviceId = source.DeviceId,
                DeviceName = source.DeviceName,
                OsVersion = source.OsVersion,
                AppVersion = source.AppVersion,
                LastSeen = source.LastSeen,
                IsPaired = source.IsPaired,
                TransportType = source.TransportType,
                ProxyEndpoint = source.ProxyEndpoint,
                GatewayEndpoint = source.GatewayEndpoint
            };
        }
    }
}
