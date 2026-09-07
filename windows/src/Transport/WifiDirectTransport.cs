using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Transport.WiFi
{
    /// <summary>
    /// Wi-Fi / hotspot transport backed by a TCP client.
    /// </summary>
    public class WifiDirectTransport : ITransport
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "WifiDirectTransport";
        private const int DefaultPort = 8988;

        private readonly Subject<byte[]> _incomingSubject = new();
        private readonly BehaviorSubject<ITransport.TransportState> _stateSubject =
            new(ITransport.TransportState.Disconnected);
        private readonly Subject<ITransport.TransportError> _errorSubject = new();

        private ITransport.TransportState _currentState = ITransport.TransportState.Disconnected;
        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private StreamReader? _reader;
        private CancellationTokenSource? _receiveCancellation;
        private WifiDirectPeerConnection? _wifiDirectConnection;

        public WifiDirectTransport()
        {
            Logger.Info(Tag, "Wi-Fi transport initialized");
        }

        public async Task<bool> ConnectAsync(string target)
        {
            WifiDirectPeerConnection? wifiDirectConnection = null;

            try
            {
                await DisconnectAsync();
                await UpdateStateAsync(ITransport.TransportState.Connecting);

                string host;
                int port;

                if (WifiDirectWinRtConnector.IsEncodedTarget(target))
                {
                    wifiDirectConnection = await WifiDirectWinRtConnector.ConnectAsync(target);
                    host = wifiDirectConnection.RemoteAddress;
                    port = DefaultPort;
                }
                else if (!TryParseTarget(target, out host, out port))
                {
                    ReportError(2001, $"Invalid Wi-Fi target: {target}");
                    await UpdateStateAsync(ITransport.TransportState.Error);
                    return false;
                }

                Logger.Info(Tag, $"Connecting to Wi-Fi endpoint {host}:{port}");

                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                await _tcpClient.ConnectAsync(host, port);
                _wifiDirectConnection = wifiDirectConnection;

                _networkStream = _tcpClient.GetStream();
                _reader = new StreamReader(_networkStream, Encoding.UTF8, false, 1024, leaveOpen: true);
                _receiveCancellation = new CancellationTokenSource();

                await UpdateStateAsync(ITransport.TransportState.Connected);
                _ = ReceiveLoopAsync(_receiveCancellation.Token);

                Logger.Info(Tag, $"Connected to Wi-Fi endpoint {host}:{port}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Wi-Fi connect error: {ex.Message}", ex);
                wifiDirectConnection?.Dispose();
                ReportError(2002, "Wi-Fi connect error", ex);
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
                    await UpdateStateAsync(ITransport.TransportState.Disconnecting);
                }

                _receiveCancellation?.Cancel();
                _reader?.Dispose();
                _networkStream?.Dispose();
                _tcpClient?.Close();
                _tcpClient?.Dispose();
                _wifiDirectConnection?.Dispose();

                _receiveCancellation = null;
                _reader = null;
                _networkStream = null;
                _tcpClient = null;
                _wifiDirectConnection = null;

                await UpdateStateAsync(ITransport.TransportState.Disconnected);
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Wi-Fi disconnect error: {ex.Message}", ex);
                ReportError(2003, "Wi-Fi disconnect error", ex);
            }
        }

        public async Task<bool> SendAsync(byte[] data)
        {
            try
            {
                if (_networkStream == null || _tcpClient?.Connected != true)
                {
                    Logger.Error(Tag, "Wi-Fi stream is not connected");
                    return false;
                }

                await _networkStream.WriteAsync(data.AsMemory(0, data.Length));
                await _networkStream.WriteAsync(new byte[] { (byte)'\n' });
                await _networkStream.FlushAsync();

                Logger.Debug(Tag, $"Sent {data.Length} bytes via Wi-Fi");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Wi-Fi send error: {ex.Message}", ex);
                ReportError(2004, "Wi-Fi send error", ex);
                await UpdateStateAsync(ITransport.TransportState.Error);
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

        public ITransport.TransportState GetState()
        {
            return _currentState;
        }

        public bool IsConnected()
        {
            return _currentState == ITransport.TransportState.Connected && _tcpClient?.Connected == true;
        }

        public string GetTransportType()
        {
            return "Wi-Fi / Hotspot";
        }

        public async Task CleanupAsync()
        {
            Logger.Info(Tag, "Cleaning up Wi-Fi transport");
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
                        Logger.Info(Tag, "Wi-Fi remote endpoint closed the connection");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var payload = Encoding.UTF8.GetBytes(line);
                    _incomingSubject.OnNext(payload);
                    Logger.Debug(Tag, $"Received {payload.Length} bytes via Wi-Fi");
                }
            }
            catch (ObjectDisposedException)
            {
                Logger.Debug(Tag, "Wi-Fi receive loop disposed");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Wi-Fi receive error: {ex.Message}", ex);
                ReportError(2005, "Wi-Fi receive error", ex);
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

        private static bool TryParseTarget(string target, out string host, out int port)
        {
            host = string.Empty;
            port = DefaultPort;

            var trimmedTarget = target.Trim();
            if (string.IsNullOrWhiteSpace(trimmedTarget))
            {
                return false;
            }

            var hostToken = trimmedTarget;
            if (trimmedTarget.Contains(':'))
            {
                var parts = trimmedTarget.Split(':', 2, StringSplitOptions.TrimEntries);
                hostToken = parts[0];
                if (!int.TryParse(parts[1], out port))
                {
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(hostToken))
            {
                return false;
            }

            if (!IPAddress.TryParse(hostToken, out _) &&
                Uri.CheckHostName(hostToken) == UriHostNameType.Unknown)
            {
                return false;
            }

            host = hostToken;
            return true;
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
