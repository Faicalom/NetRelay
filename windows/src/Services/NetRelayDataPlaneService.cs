using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Services
{
    /// <summary>
    /// Orchestrates the v2 data plane:
    /// tun2socks -> local SOCKS5 -> AEL2 multiplexed stream -> Android socket engine.
    /// </summary>
    public sealed class NetRelayDataPlaneService : IAsyncDisposable
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "NetRelayDataPlane";

        private readonly NetRelayGatewayClient _gatewayClient = new();
        private readonly Socks5GatewayServer _socksServer;

        private string? _mode;
        private string? _target;
        private string? _transportRouteEndpoint;

        public NetRelayDataPlaneService()
        {
            _socksServer = new Socks5GatewayServer(_gatewayClient);
            _gatewayClient.ActiveFlowCountProvider = () => _socksServer.ActiveFlowCount;
        }

        public bool IsRunning => _gatewayClient.IsConnected && _socksServer.IsRunning;
        public string LocalSocksEndpoint => _socksServer.Endpoint;
        public string TransportTarget => _transportRouteEndpoint ?? string.Empty;
        public Socks5GatewayServer.SocksFlowMetrics FlowMetrics => _socksServer.FlowMetrics;

        public async Task<string> StartUsbAsync(
            AdbBridgeService adb,
            string? serial,
            CancellationToken cancellationToken = default)
        {
            Logger.Info(Tag, "[NETRELAY-USB] Preparing V2 data plane");
            Logger.Info(Tag, "[NETRELAY-USB] Calling StartForTransportAsync usb");
            await StopAsync();
            var bridge = await adb.PrepareNetRelayUsbDataTunnelAsync(serial);
            Logger.Info(Tag, "[NETRELAY-USB] Testing 127.0.0.1:8992");
            using (var probe = new TcpClient())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await probe.ConnectAsync("127.0.0.1", AdbBridgeService.LocalDataTunnelPort, timeout.Token);
                    Logger.Info(Tag, "[NETRELAY-USB] 8992 reachable");
                }
                catch (Exception ex)
                {
                    Logger.Error(Tag, $"[NETRELAY-USB] 8992 unreachable: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}", ex);
                    throw;
                }
            }
            await _gatewayClient.ConnectAsync("127.0.0.1", AdbBridgeService.LocalDataTunnelPort, cancellationToken);
            await _socksServer.StartAsync(cancellationToken);
            Logger.Info(Tag, "[NETRELAY-USB] SOCKS 8990 listening");
            _mode = "usb";
            _target = bridge.Serial;
            _transportRouteEndpoint = "127.0.0.1";
            Logger.Info(Tag, $"USB v2 data plane active for {bridge.Serial}");
            Logger.Info(Tag, "[NETRELAY-USB] DATA PLANE READY");
            return LocalSocksEndpoint;
        }

        public async Task<string> StartWifiAsync(
            string endpoint,
            CancellationToken cancellationToken = default)
        {
            await StopAsync();
            var host = ExtractHost(endpoint);
            Logger.Info(Tag, $"[NETRELAY-WFD] Android P2P endpoint={host}:{AndroidGatewayPort}");
            using (var probe = new TcpClient(AddressFamily.InterNetwork))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await probe.ConnectAsync(host, AndroidGatewayPort, timeout.Token);
                    Logger.Info(Tag, "[NETRELAY-WFD] Endpoint reachable");
                }
                catch (Exception ex)
                {
                    Logger.Error(Tag, $"[NETRELAY-WFD] Endpoint unreachable: {ex.Message}", ex);
                    throw new InvalidOperationException(
                        $"Wi-Fi Direct Android endpoint {host}:{AndroidGatewayPort} is unreachable; SOCKS/Wintun were not started.",
                        ex);
                }
            }
            Logger.Info(Tag, "[NETRELAY-WFD] Opening AEL2 session");
            await _gatewayClient.ConnectAsync(host, AndroidGatewayPort, cancellationToken);
            await _socksServer.StartAsync(cancellationToken);
            _mode = "wifidirect";
            _target = host;
            _transportRouteEndpoint = host;
            Logger.Info(Tag, $"Wi-Fi v2 data plane active through {host}:{AndroidGatewayPort}");
            Logger.Info(Tag, "[NETRELAY-WFD] Wi-Fi Direct data plane active");
            return LocalSocksEndpoint;
        }

        public async Task<string> StartForTransportAsync(
            string transportType,
            string target,
            AdbBridgeService adb,
            CancellationToken cancellationToken = default)
        {
            var normalizedMode = transportType.ToLowerInvariant();
            var normalizedTarget = normalizedMode == "wifidirect" || normalizedMode == "wifi"
                ? ExtractHost(target)
                : target.Trim();

            if (IsRunning &&
                string.Equals(_mode, normalizedMode, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(normalizedTarget) ||
                 string.Equals(_target, normalizedTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return LocalSocksEndpoint;
            }

            return normalizedMode switch
            {
                "usb" => await StartUsbAsync(adb, target, cancellationToken),
                _ => await StartWifiAsync(target, cancellationToken)
            };
        }

        public async Task StopAsync()
        {
            await _socksServer.StopAsync();
            await _gatewayClient.DisconnectAsync();
            _mode = null;
            _target = null;
            _transportRouteEndpoint = null;
        }

        private static string ExtractHost(string endpoint)
        {
            var value = endpoint.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                return uri.Host;

            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                var end = value.IndexOf(']');
                if (end > 1) return value.Substring(1, end - 1);
            }

            var colon = value.LastIndexOf(':');
            if (colon > 0 && value.IndexOf(':') == colon)
                return value.Substring(0, colon);
            return value;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _socksServer.DisposeAsync();
            await _gatewayClient.DisposeAsync();
        }

        public const int AndroidGatewayPort = 8991;
    }
}
