using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Services
{
    public sealed class NetworkAdapterModeService : IDisposable
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "NetworkAdapterModeService";
        private const string AdapterName = "NetRelay";
        private static readonly string[] LegacyAdapterNames =
        {
            "wintun",
            "NetRelay",
            "NetRelay Broadband Connection"
        };
        private const string AdapterAddress = "192.168.123.1";
        private const int AdapterPrefixLength = 30;
        private const string AdapterMask = "255.255.255.252";
        private const string AdapterRouteNextHop = "192.168.123.2";
        private const string LegacyOnLinkNextHop = "0.0.0.0";
        private const int AdapterMtu = 1280;
        private const string LegacyQuicBlockRuleName = "NetRelay Block QUIC UDP 443";
        private const string NativePrimaryDns = "1.1.1.1";
        private const string NativeSecondaryDns = "8.8.8.8";
        private static readonly string[] DefaultRoutePrefixes =
        {
            "0.0.0.0/0",
            "0.0.0.0/1",
            "128.0.0.0/1"
        };

        private Process? _tunProcess;
        private TransportExceptionRoute? _transportExceptionRoute;

        public bool IsRunning => _tunProcess is { HasExited: false };

        public async Task<NetworkAdapterModeResult> StartAsync(
            string proxyEndpoint,
            string? transportEndpoint = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(proxyEndpoint))
            {
                throw new InvalidOperationException("عنوان بروكسي الهاتف غير متاح بعد.");
            }

            if (!IsAdministrator())
            {
                throw new InvalidOperationException("وضع المحول يحتاج تشغيل NetRelay كمسؤول.");
            }

            var gateway = NetworkGatewayEndpoint.Parse(proxyEndpoint);
            var proxyUri = gateway.Uri;
            var tools = ResolveTools();

            await StopAsync();

            try
            {
                _tunProcess = StartTun2Socks(tools, gateway);
                await WaitForAdapterAsync(cancellationToken);

                var effectiveTransportEndpoint = string.IsNullOrWhiteSpace(transportEndpoint)
                    ? proxyUri.Host
                    : transportEndpoint;
                _transportExceptionRoute = ResolveTransportExceptionRoute(effectiveTransportEndpoint);
                await InstallTransportExceptionRouteAsync(_transportExceptionRoute, cancellationToken);

                await RunNetshAsync(
                    $"interface ipv4 set address name=\"{AdapterName}\" source=static addr={AdapterAddress} mask={AdapterMask}",
                    cancellationToken: cancellationToken);

                await RunNetshAsync(
                    $"interface ipv4 set subinterface \"{AdapterName}\" mtu={AdapterMtu} store=active",
                    allowFailure: true,
                    cancellationToken: cancellationToken);

                await WaitForAdapterAddressAsync(cancellationToken);
                Logger.Info(Tag, $"[NETRELAY-NCSI] Wintun address={AdapterAddress}/{AdapterPrefixLength}");
                Logger.Info(Tag, $"[NETRELAY-NCSI] Wintun nextHop={AdapterRouteNextHop}");

                // DNS and other UDP traffic are sent through SOCKS5 by tun2socks.
                await RunNetshAsync(
                    $"interface ipv4 set dnsservers name=\"{AdapterName}\" static address={NativePrimaryDns} register=none validate=no",
                    cancellationToken: cancellationToken);
                await RunNetshAsync(
                    $"interface ipv4 add dnsservers name=\"{AdapterName}\" address={NativeSecondaryDns} index=2 validate=no",
                    allowFailure: true,
                    cancellationToken: cancellationToken);
                var dnsEndpoint = $"{NativePrimaryDns}, {NativeSecondaryDns} through SOCKS5";

                await RunNetshAsync(
                    $"interface ipv4 set interface \"{AdapterName}\" metric=1",
                    cancellationToken: cancellationToken);

                foreach (var routePrefix in DefaultRoutePrefixes)
                {
                    await RunNetshAsync(
                        $"interface ipv4 delete route {routePrefix} \"{AdapterName}\" {AdapterAddress}",
                        allowFailure: true,
                        cancellationToken: cancellationToken);
                    await RunNetshAsync(
                        $"interface ipv4 delete route {routePrefix} \"{AdapterName}\" {AdapterRouteNextHop}",
                        allowFailure: true,
                        cancellationToken: cancellationToken);
                    await RunNetshAsync(
                        $"interface ipv4 delete route {routePrefix} \"{AdapterName}\" {LegacyOnLinkNextHop}",
                        allowFailure: true,
                        cancellationToken: cancellationToken);
                }

                await RunNetshAsync(
                    $"interface ipv4 add route 0.0.0.0/0 \"{AdapterName}\" {AdapterRouteNextHop} metric=1",
                    allowFailure: true,
                    cancellationToken: cancellationToken);

                await RunNetshAsync(
                    $"interface ipv4 add route 0.0.0.0/1 \"{AdapterName}\" {AdapterRouteNextHop} metric=1",
                    cancellationToken: cancellationToken);

                await RunNetshAsync(
                    $"interface ipv4 add route 128.0.0.0/1 \"{AdapterName}\" {AdapterRouteNextHop} metric=1",
                    cancellationToken: cancellationToken);
                Logger.Info(Tag, "[NETRELAY-NCSI] Default route ready");

                // Native SOCKS5 carries UDP. Remove a stale rule left by pre-release builds.
                await RemoveQuicBlockRuleAsync(cancellationToken);

                await RefreshWindowsConnectivityIndicatorAsync(cancellationToken);

                Logger.Info(
                    Tag,
                    $"Adapter mode started through {gateway.DisplayName} at {proxyUri.Host}:{proxyUri.Port}");
                return new NetworkAdapterModeResult
                {
                    AdapterName = AdapterName,
                    AdapterAddress = AdapterAddress,
                    ProxyEndpoint = gateway.Uri.ToString(),
                    DnsEndpoint = dnsEndpoint,
                    GatewayMode = gateway.Kind.ToString(),
                    SupportsUdp = true
                };
            }
            catch
            {
                await StopAsync();
                throw;
            }
        }

        public async Task StopAsync()
        {
            await RemoveQuicBlockRuleAsync();

            if (_transportExceptionRoute is { RequiresHostRoute: true } exceptionRoute)
            {
                await RunNetshAsync(
                    $"interface ipv4 delete route {exceptionRoute.Host}/32 \"{exceptionRoute.InterfaceAlias}\" {exceptionRoute.NextHop}",
                    allowFailure: true);
            }
            _transportExceptionRoute = null;

            foreach (var routePrefix in DefaultRoutePrefixes)
            {
                await RunNetshAsync(
                    $"interface ipv4 delete route {routePrefix} \"{AdapterName}\" {AdapterAddress}",
                    allowFailure: true);
                await RunNetshAsync(
                    $"interface ipv4 delete route {routePrefix} \"{AdapterName}\" {AdapterRouteNextHop}",
                    allowFailure: true);
                await RunNetshAsync(
                    $"interface ipv4 delete route {routePrefix} \"{AdapterName}\" {LegacyOnLinkNextHop}",
                    allowFailure: true);
            }

            foreach (var legacyAdapterName in LegacyAdapterNames)
            {
                foreach (var routePrefix in DefaultRoutePrefixes)
                {
                    await RunNetshAsync(
                        $"interface ipv4 delete route {routePrefix} \"{legacyAdapterName}\" {AdapterAddress}",
                        allowFailure: true);
                    await RunNetshAsync(
                        $"interface ipv4 delete route {routePrefix} \"{legacyAdapterName}\" {AdapterRouteNextHop}",
                        allowFailure: true);
                    await RunNetshAsync(
                        $"interface ipv4 delete route {routePrefix} \"{legacyAdapterName}\" {LegacyOnLinkNextHop}",
                        allowFailure: true);
                }
            }

            if (_tunProcess != null)
            {
                try
                {
                    if (!_tunProcess.HasExited)
                    {
                        _tunProcess.Kill(entireProcessTree: true);
                        await _tunProcess.WaitForExitAsync();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(Tag, $"Failed to stop tun2socks cleanly: {ex.Message}");
                }
                finally
                {
                    _tunProcess.Dispose();
                    _tunProcess = null;
                }
            }
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        private static NetworkAdapterTools ResolveTools()
        {
            foreach (var directory in GetToolDirectories())
            {
                var tun2SocksPath = Path.Combine(directory, "tun2socks.exe");
                var wintunPath = Path.Combine(directory, "wintun.dll");
                if (File.Exists(tun2SocksPath) && File.Exists(wintunPath))
                {
                    return new NetworkAdapterTools(directory, tun2SocksPath, wintunPath);
                }
            }

            throw new FileNotFoundException(
                "ملفات وضع المحول ناقصة. ضع tun2socks.exe و wintun.dll داخل tools\\netmode.");
        }

        private static IEnumerable<string> GetToolDirectories()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "tools", "netmode");
            yield return Path.Combine(Directory.GetCurrentDirectory(), "tools", "netmode");

            foreach (var ancestor in GetAncestorDirectories(AppContext.BaseDirectory))
            {
                yield return Path.Combine(ancestor, "tools", "netmode");
            }
        }

        private static IEnumerable<string> GetAncestorDirectories(string startDirectory)
        {
            var current = new DirectoryInfo(startDirectory);
            while (current != null)
            {
                yield return current.FullName;
                current = current.Parent;
            }
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static Process StartTun2Socks(NetworkAdapterTools tools, NetworkGatewayEndpoint gateway)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = tools.Tun2SocksPath,
                WorkingDirectory = tools.Directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-device");
            startInfo.ArgumentList.Add(AdapterName);
            startInfo.ArgumentList.Add("-proxy");
            startInfo.ArgumentList.Add(gateway.Uri.ToString().TrimEnd('/'));
            startInfo.ArgumentList.Add("-mtu");
            startInfo.ArgumentList.Add(AdapterMtu.ToString());
            startInfo.ArgumentList.Add("-tcp-auto-tuning");
            startInfo.ArgumentList.Add("-udp-timeout");
            startInfo.ArgumentList.Add("2s");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("info");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            CaptureProcessOutput(process);
            return process;
        }

        private static TransportExceptionRoute ResolveTransportExceptionRoute(string endpoint)
        {
            var host = ExtractEndpointHost(endpoint);
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException(
                    $"Transport endpoint '{host}' is not an IPv4 address; refusing to create Internet routes without a loop-prevention route.");
            }

            if (IPAddress.IsLoopback(address))
            {
                return new TransportExceptionRoute(host, "Loopback", LegacyOnLinkNextHop, false);
            }

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface =>
                    networkInterface.OperationalStatus == OperationalStatus.Up &&
                    !string.Equals(networkInterface.Name, AdapterName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var networkInterface in interfaces)
            {
                var properties = networkInterface.GetIPProperties();
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IsAddressInPrefix(address, unicast.Address, unicast.PrefixLength))
                    {
                        return new TransportExceptionRoute(
                            host,
                            networkInterface.Name,
                            LegacyOnLinkNextHop,
                            true);
                    }
                }
            }

            // Compatibility fallback for a non-local transport endpoint: ask the
            // current pre-Wintun routing table which physical source address it uses.
            using var routeProbe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            routeProbe.Connect(new IPEndPoint(address, 9));
            var localAddress = (routeProbe.LocalEndPoint as IPEndPoint)?.Address;
            var selectedInterface = interfaces.FirstOrDefault(networkInterface =>
                networkInterface.GetIPProperties().UnicastAddresses.Any(unicast =>
                    Equals(unicast.Address, localAddress)));
            var gateway = selectedInterface?.GetIPProperties().GatewayAddresses
                .Select(item => item.Address)
                .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);

            if (selectedInterface == null || gateway == null)
            {
                throw new InvalidOperationException(
                    $"No physical route was found for transport endpoint {host}; Wintun default routes were not installed.");
            }

            return new TransportExceptionRoute(
                host,
                selectedInterface.Name,
                gateway.ToString(),
                true);
        }

        private static async Task InstallTransportExceptionRouteAsync(
            TransportExceptionRoute route,
            CancellationToken cancellationToken)
        {
            if (route.RequiresHostRoute)
            {
                await RunNetshAsync(
                    $"interface ipv4 delete route {route.Host}/32 \"{route.InterfaceAlias}\" {route.NextHop}",
                    allowFailure: true,
                    cancellationToken: cancellationToken);
                await RunNetshAsync(
                    $"interface ipv4 add route {route.Host}/32 \"{route.InterfaceAlias}\" {route.NextHop} metric=1 store=active",
                    cancellationToken: cancellationToken);
            }

            Logger.Info(
                Tag,
                $"[NETRELAY-NCSI] Transport exception route ready host={route.Host} interface={route.InterfaceAlias} nextHop={route.NextHop}");
        }

        private static string ExtractEndpointHost(string endpoint)
        {
            var value = endpoint.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                var bracket = value.IndexOf(']');
                if (bracket > 1)
                {
                    return value[1..bracket];
                }
            }

            var colon = value.LastIndexOf(':');
            return colon > 0 && value.IndexOf(':') == colon
                ? value[..colon]
                : value;
        }

        private static bool IsAddressInPrefix(IPAddress candidate, IPAddress local, int prefixLength)
        {
            if (prefixLength is < 0 or > 32)
            {
                return false;
            }

            var candidateValue = ToUInt32(candidate);
            var localValue = ToUInt32(local);
            var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
            return (candidateValue & mask) == (localValue & mask);
        }

        private static uint ToUInt32(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) |
                   ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) |
                   bytes[3];
        }

        private static void CaptureProcessOutput(Process process)
        {
            _ = Task.Run(async () =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Logger.Debug(Tag, $"tun2socks: {line}");
                    }
                }
            });

            _ = Task.Run(async () =>
            {
                while (!process.StandardError.EndOfStream)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Logger.Warn(Tag, $"tun2socks: {line}");
                    }
                }
            });
        }

        private async Task WaitForAdapterAsync(CancellationToken cancellationToken)
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_tunProcess?.HasExited == true)
                {
                    throw new InvalidOperationException("tun2socks توقف قبل إنشاء محول Wintun.");
                }

                var result = await RunNetshAsync(
                    "interface ipv4 show interfaces",
                    allowFailure: true,
                    cancellationToken: cancellationToken);

                if (result.StandardOutput.Contains(AdapterName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await Task.Delay(300, cancellationToken);
            }

            throw new TimeoutException("لم يظهر محول Wintun خلال المهلة المحددة.");
        }

        private static async Task WaitForAdapterAddressAsync(CancellationToken cancellationToken)
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(8);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await RunNetshAsync(
                    $"interface ipv4 show addresses name=\"{AdapterName}\"",
                    allowFailure: true,
                    cancellationToken: cancellationToken);

                if (result.StandardOutput.Contains(AdapterAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await Task.Delay(300, cancellationToken);
            }

            throw new TimeoutException("تم إنشاء محول Wintun لكن Windows لم يثبت عنوانه المحلي بعد.");
        }

        private static async Task<ProcessResult> RunNetshAsync(
            string arguments,
            bool allowFailure = false,
            CancellationToken cancellationToken = default)
        {
            return await RunProcessAsync("netsh.exe", arguments, allowFailure, cancellationToken);
        }

        private static async Task RemoveQuicBlockRuleAsync(CancellationToken cancellationToken = default)
        {
            await RunNetshAsync(
                $"advfirewall firewall delete rule name=\"{LegacyQuicBlockRuleName}\"",
                allowFailure: true,
                cancellationToken: cancellationToken);
        }

        private static async Task RefreshWindowsConnectivityIndicatorAsync(CancellationToken cancellationToken)
        {
            // Do not persist NCSI policy/registry changes. Once the virtual adapter has
            // working routes and DNS, Windows can probe connectivity normally.
            await RunProcessAsync("sc.exe", "start netprofm", allowFailure: true, cancellationToken);
            await RunProcessAsync("sc.exe", "start NlaSvc", allowFailure: true, cancellationToken);
            await RunProcessAsync("ipconfig.exe", "/flushdns", allowFailure: true, cancellationToken);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var diagnosticTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await RunNcsiDiagnosticsAsync(diagnosticTimeout.Token);
                }
                catch (Exception ex)
                {
                    Logger.Debug(Tag, $"[NETRELAY-NCSI] Diagnostics stopped: {ex.Message}");
                }
            });
        }

        private static async Task RunNcsiDiagnosticsAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await LogNcsiProfilesAsync("before-probe", cancellationToken);

            var connectTestDnsSuccess =
                await ResolveNcsiHostAsync("www.msftconnecttest.com", cancellationToken);
            var ncsiDnsSuccess =
                await ResolveNcsiHostAsync("dns.msftncsi.com", cancellationToken);
            var dnsProbeSuccess = connectTestDnsSuccess && ncsiDnsSuccess;
            Logger.Info(Tag, $"[NETRELAY-NCSI] DNS probe success={dnsProbeSuccess.ToString().ToLowerInvariant()}");

            var httpProbeSuccess = false;
            var responseValid = false;
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };
                using var response = await client.GetAsync(
                    "http://www.msftconnecttest.com/connecttest.txt",
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                httpProbeSuccess = response.IsSuccessStatusCode;
                responseValid = string.Equals(
                    body.Trim(),
                    "Microsoft Connect Test",
                    StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.Debug(Tag, $"[NETRELAY-NCSI] HTTP probe exception={ex.GetType().Name}");
            }

            Logger.Info(Tag, $"[NETRELAY-NCSI] HTTP probe success={httpProbeSuccess.ToString().ToLowerInvariant()}");
            Logger.Info(Tag, $"[NETRELAY-NCSI] Response valid={responseValid.ToString().ToLowerInvariant()}");

            // NCSI updates the profile asynchronously after its own active probe. The
            // delay only observes the natural result; it does not change NCSI policy.
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            await LogNcsiProfilesAsync("after-probe", cancellationToken);
            await LogRecentNcsiEventsAsync(cancellationToken);
        }

        private static async Task<bool> ResolveNcsiHostAsync(
            string host,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
                return addresses.Length > 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.Debug(Tag, $"[NETRELAY-NCSI] DNS exception={ex.GetType().Name}");
                return false;
            }
        }

        private static async Task LogNcsiProfilesAsync(
            string stage,
            CancellationToken cancellationToken)
        {
            const string script =
                "$ErrorActionPreference='Stop'; " +
                "Get-NetConnectionProfile | ForEach-Object { " +
                "[pscustomobject]@{ " +
                "InterfaceAlias=$_.InterfaceAlias; " +
                "InterfaceIndex=$_.InterfaceIndex; " +
                "IPv4Connectivity=$_.IPv4Connectivity.ToString(); " +
                "IPv6Connectivity=$_.IPv6Connectivity.ToString(); " +
                "NetworkCategory=$_.NetworkCategory.ToString() } " +
                "} | ConvertTo-Json -Compress";

            var result = await RunPowerShellAsync(script, cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                Logger.Warn(Tag, $"[NETRELAY-NCSI] Get-NetConnectionProfile unavailable stage={stage}");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                var root = document.RootElement;
                var profiles = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().ToArray()
                    : new[] { root };

                foreach (var profile in profiles)
                {
                    var interfaceAlias = GetJsonText(profile, "InterfaceAlias");
                    var ipv4Connectivity = GetJsonText(profile, "IPv4Connectivity");
                    Logger.Info(
                        Tag,
                        $"[NETRELAY-NCSI] Interface={interfaceAlias} " +
                        $"InterfaceIndex={GetJsonText(profile, "InterfaceIndex")} " +
                        $"IPv4Connectivity={ipv4Connectivity} " +
                        $"IPv6Connectivity={GetJsonText(profile, "IPv6Connectivity")} " +
                        $"NetworkCategory={GetJsonText(profile, "NetworkCategory")} " +
                        $"Stage={stage}");
                    if (string.Equals(interfaceAlias, AdapterName, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info(Tag, $"[NETRELAY-NCSI] IPv4Connectivity={ipv4Connectivity}");
                    }
                }
            }
            catch (JsonException ex)
            {
                Logger.Warn(Tag, $"[NETRELAY-NCSI] Profile output parse failed: {ex.Message}");
            }
        }

        private static async Task LogRecentNcsiEventsAsync(CancellationToken cancellationToken)
        {
            const string script =
                "$ErrorActionPreference='Stop'; " +
                "$start=(Get-Date).AddMinutes(-2); " +
                "Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-NCSI/Operational';StartTime=$start} " +
                "| Select-Object -First 8 " +
                "@{Name='TimeCreated';Expression={$_.TimeCreated.ToString('o')}},Id," +
                "@{Name='Message';Expression={($_.Message -replace '[\\r\\n]+',' ').Trim()}} " +
                "| ConvertTo-Json -Compress";

            var result = await RunPowerShellAsync(script, cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                Logger.Debug(Tag, "[NETRELAY-NCSI] Operational event log unavailable");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                var root = document.RootElement;
                var events = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().ToArray()
                    : new[] { root };

                foreach (var ncsiEvent in events)
                {
                    var message = GetJsonText(ncsiEvent, "Message");
                    if (message.Length > 300)
                    {
                        message = message[..300];
                    }

                    Logger.Info(
                        Tag,
                        $"[NETRELAY-NCSI] Event time={GetJsonText(ncsiEvent, "TimeCreated")} " +
                        $"Id={GetJsonText(ncsiEvent, "Id")} Message={message}");
                }
            }
            catch (JsonException ex)
            {
                Logger.Warn(Tag, $"[NETRELAY-NCSI] Event output parse failed: {ex.Message}");
            }
        }

        private static Task<ProcessResult> RunPowerShellAsync(
            string script,
            CancellationToken cancellationToken)
        {
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return RunProcessAsync(
                "powershell.exe",
                $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
                allowFailure: true,
                cancellationToken);
        }

        private static string GetJsonText(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return "unknown";
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? "unknown"
                : property.ToString();
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string fileName,
            string arguments,
            bool allowFailure,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var result = new ProcessResult(
                process.ExitCode,
                await outputTask,
                await errorTask);

            if (!allowFailure && result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{fileName} {arguments} فشل: {result.ErrorOutputOrFallback}");
            }

            return result;
        }

        private sealed record NetworkAdapterTools(
            string Directory,
            string Tun2SocksPath,
            string WintunPath);

        private sealed record TransportExceptionRoute(
            string Host,
            string InterfaceAlias,
            string NextHop,
            bool RequiresHostRoute);

        private sealed record ProcessResult(
            int ExitCode,
            string StandardOutput,
            string StandardError)
        {
            public string ErrorOutputOrFallback =>
                string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        }
    }

    public sealed class NetworkAdapterModeResult
    {
        public string AdapterName { get; set; } = string.Empty;
        public string AdapterAddress { get; set; } = string.Empty;
        public string ProxyEndpoint { get; set; } = string.Empty;
        public string DnsEndpoint { get; set; } = string.Empty;
        public string GatewayMode { get; set; } = string.Empty;
        public bool SupportsUdp { get; set; }
    }

}
