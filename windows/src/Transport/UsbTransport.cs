using System;
using System.IO;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Services;
using NetRelay.Utils;

namespace NetRelay.Transport.Usb
{
    /// <summary>
    /// USB transport backed by adb port forwarding to the Android app.
    /// </summary>
    public class UsbTransport : ITransport
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "UsbTransport";
        private readonly Subject<byte[]> _incomingSubject = new();
        private readonly BehaviorSubject<ITransport.TransportState> _stateSubject =
            new(ITransport.TransportState.Disconnected);
        private readonly Subject<ITransport.TransportError> _errorSubject = new();
        private readonly AdbBridgeService _adbBridgeService = new();

        private ITransport.TransportState _currentState = ITransport.TransportState.Disconnected;
        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private StreamReader? _reader;
        private CancellationTokenSource? _receiveCancellation;
        private string? _activeSerial;

        public UsbTransport()
        {
            Logger.Info(Tag, "USB transport initialized");
        }

        public async Task<bool> ConnectAsync(string target)
        {
            try
            {
                Logger.Info(Tag, "[NETRELAY-USB] UsbTransport.ConnectAsync entered");
                await DisconnectAsync();
                await UpdateStateAsync(ITransport.TransportState.Connecting);

                var preparedBridge = await _adbBridgeService.PrepareNetRelayUsbAsync(target);
                _activeSerial = preparedBridge.Serial;
                Logger.Info(Tag, $"ADB bridge ready for {_activeSerial}");

                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                await _tcpClient.ConnectAsync("127.0.0.1", 8988);

                _networkStream = _tcpClient.GetStream();
                _reader = new StreamReader(_networkStream, Encoding.UTF8, false, 1024, leaveOpen: true);
                _receiveCancellation = new CancellationTokenSource();

                await UpdateStateAsync(ITransport.TransportState.Connected);
                _ = ReceiveLoopAsync(_receiveCancellation.Token);

                Logger.Info(Tag, "USB connection established over adb forward");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"[NETRELAY-USB] FAILED stage=UsbTransport.ConnectAsync type={ex.GetType().FullName} message={ex.Message}\n{ex.StackTrace}", ex);
                Logger.Error(Tag, $"USB connect error: {ex.Message}", ex);
                ReportError(2101, "USB connect error", ex);
                await UpdateStateAsync(ITransport.TransportState.Error);
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                if (_currentState != ITransport.TransportState.Disconnected)
                {
                    Logger.Info(Tag, "Disconnecting USB");
                    await UpdateStateAsync(ITransport.TransportState.Disconnecting);
                }

                _receiveCancellation?.Cancel();
                _reader?.Dispose();
                _networkStream?.Dispose();
                _tcpClient?.Close();
                _tcpClient?.Dispose();

                _receiveCancellation = null;
                _reader = null;
                _networkStream = null;
                _tcpClient = null;

                await _adbBridgeService.CleanupNetRelayUsbAsync(_activeSerial);
                _activeSerial = null;
                await UpdateStateAsync(ITransport.TransportState.Disconnected);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"USB disconnect error: {ex.Message}", ex);
            }
        }

        public async Task<bool> SendAsync(byte[] data)
        {
            try
            {
                if (_networkStream == null || _tcpClient?.Connected != true)
                {
                    Logger.Error(Tag, "USB stream is not connected");
                    return false;
                }

                Logger.Debug(Tag, $"Sending {data.Length} bytes via USB");
                await _networkStream.WriteAsync(data.AsMemory(0, data.Length));
                await _networkStream.WriteAsync(new byte[] { (byte)'\n' });
                await _networkStream.FlushAsync();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"USB send error: {ex.Message}", ex);
                ReportError(2102, "USB send error", ex);
                return false;
            }
        }

        public IObservable<byte[]> ObserveIncoming()
        {
            return _incomingSubject.AsObservable();
        }

        public IObservable<ITransport.TransportState> ObserveState()
        {
            return _stateSubject.AsObservable();
        }

        public IObservable<ITransport.TransportError> ObserveErrors()
        {
            return _errorSubject.AsObservable();
        }

        public ITransport.TransportState GetState() => _currentState;

        public bool IsConnected() =>
            _currentState == ITransport.TransportState.Connected && _tcpClient?.Connected == true;

        public string GetTransportType() => "USB / ADB";

        public async Task CleanupAsync()
        {
            Logger.Info(Tag, "Cleaning up USB transport");
            await DisconnectAsync();
        }

        public void Dispose()
        {
            _receiveCancellation?.Cancel();
            _reader?.Dispose();
            _networkStream?.Dispose();
            _tcpClient?.Dispose();
            _incomingSubject.Dispose();
            _stateSubject.Dispose();
            _errorSubject.Dispose();
        }

        private async Task UpdateStateAsync(ITransport.TransportState state)
        {
            _currentState = state;
            _stateSubject.OnNext(state);
            Logger.Debug(Tag, $"State changed to: {state}");
            await Task.CompletedTask;
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _reader != null)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line == null)
                    {
                        Logger.Info(Tag, "USB remote endpoint closed the connection");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var payload = Encoding.UTF8.GetBytes(line);
                    _incomingSubject.OnNext(payload);
                    Logger.Debug(Tag, $"Received {payload.Length} bytes via USB");
                }
            }
            catch (ObjectDisposedException)
            {
                Logger.Debug(Tag, "USB receive loop disposed");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"USB receive error: {ex.Message}", ex);
                ReportError(2103, "USB receive error", ex);
                await UpdateStateAsync(ITransport.TransportState.Error);
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await DisconnectAsync();
                }
            }
        }

        private void ReportError(int code, string message, Exception? exception = null)
        {
            _errorSubject.OnNext(new ITransport.TransportError
            {
                Code = code,
                Message = message,
                Cause = exception
            });
        }
    }
}
