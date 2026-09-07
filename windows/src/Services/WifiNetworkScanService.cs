using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Services
{
    public sealed class WifiNetworkScanService
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "WifiNetworkScan";
        private static readonly Regex SsidLinePattern = new(
            @"^\s*SSID\s+\d+\s*:\s*(?<ssid>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IWindowsWlanActiveScanService _activeScanService;
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _networkEnumerator;
        private readonly TimeSpan _publicationWindow;
        private readonly TimeSpan _publicationPollInterval;

        public WifiNetworkScanService()
            : this(
                new WindowsWlanActiveScanService(),
                EnumerateVisibleNetworkNamesAsync,
                TimeSpan.FromMilliseconds(1500),
                TimeSpan.FromMilliseconds(250))
        {
        }

        internal WifiNetworkScanService(
            IWindowsWlanActiveScanService activeScanService,
            Func<CancellationToken, Task<IReadOnlyList<string>>> networkEnumerator,
            TimeSpan publicationWindow,
            TimeSpan publicationPollInterval)
        {
            _activeScanService = activeScanService ?? throw new ArgumentNullException(nameof(activeScanService));
            _networkEnumerator = networkEnumerator ?? throw new ArgumentNullException(nameof(networkEnumerator));
            _publicationWindow = publicationWindow;
            _publicationPollInterval = publicationPollInterval;
        }

        public async Task<IReadOnlyList<string>> GetVisibleNetworkNamesAsync(
            string? expectedSsid = null,
            CancellationToken cancellationToken = default,
            bool requestActiveScan = false)
        {
            Logger.Info(Tag, $"[NETRELAY-WFD] Expected SSID={expectedSsid ?? "<auto>"}");
            WlanActiveScanResult? activeScan = null;
            if (requestActiveScan)
            {
                activeScan = await _activeScanService.RequestScanAsync(cancellationToken);
                if (!activeScan.Succeeded)
                {
                    Logger.Warn(
                        Tag,
                        $"[NETRELAY-WFD] Cached enumeration fallback used=true reason={activeScan.FailureReason ?? "active scan did not complete"}");
                }
            }

            var publicationDeadline = DateTime.UtcNow + _publicationWindow;
            var attempt = 0;
            IReadOnlyList<string> networks;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;
                networks = await _networkEnumerator(cancellationToken);
                Logger.Info(Tag, $"[NETRELAY-WFD] Visible network count={networks.Count}");

                var foundNetwork = FindExpectedOrRelevantNetwork(networks, expectedSsid);
                if (!string.IsNullOrWhiteSpace(foundNetwork))
                {
                    Logger.Info(Tag, $"[NETRELAY-WFD] Wi-Fi Direct network found={foundNetwork}");
                    return networks;
                }

                if (!requestActiveScan || activeScan?.Succeeded != true || DateTime.UtcNow >= publicationDeadline)
                {
                    break;
                }

                Logger.Info(Tag, $"[NETRELAY-WFD] Waiting for refreshed WLAN results attempt={attempt}");
                await Task.Delay(_publicationPollInterval, cancellationToken);
            }
            while (DateTime.UtcNow < publicationDeadline);

            return networks;
        }

        public string? SelectBestNetRelayNetwork(
            IEnumerable<string> networkNames,
            string? currentSelection = null)
        {
            var candidates = networkNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(currentSelection))
            {
                var preserved = candidates.FirstOrDefault(name =>
                    string.Equals(name, currentSelection, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(preserved) && IsNetRelayNetworkName(preserved))
                {
                    return preserved;
                }
            }

            return candidates
                .OrderByDescending(name => name.Contains("NetRelay", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(name => name.StartsWith("DIRECT-", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(name => name.Contains("SOG01", StringComparison.OrdinalIgnoreCase))
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(name =>
                    name.Contains("NetRelay", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("DIRECT-", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> ConnectToNetworkAsync(
            string networkName,
            string passphrase,
            CancellationToken cancellationToken = default)
        {
            var result = await EnsureConnectedAsync(networkName, passphrase, cancellationToken);
            return result.Connected;
        }

        public async Task<WifiNetworkConnectResult> EnsureConnectedAsync(
            string networkName,
            string? passphrase,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(networkName))
            {
                throw new ArgumentException("Wi-Fi network name is required.", nameof(networkName));
            }

            var expectedSsid = networkName.Trim();
            Logger.Info(Tag, $"[NETRELAY-WFD] Expected SSID={expectedSsid}");

            var currentState = await GetCurrentConnectionStateAsync(expectedSsid, cancellationToken);
            Logger.Info(Tag, $"[NETRELAY-WFD] Current Windows SSID={currentState.Ssid ?? "<none>"}");
            if (currentState.IsConnected)
            {
                Logger.Info(Tag, "[NETRELAY-WFD] Already connected to expected SSID; skipping WLAN association");
                LogConnectedState(currentState);
                return WifiNetworkConnectResult.Success(
                    await HasSavedProfileAsync(expectedSsid, cancellationToken),
                    currentState.Ssid,
                    currentState.Ipv4Address);
            }

            var visibleNetworks = await GetVisibleNetworkNamesAsync(expectedSsid, cancellationToken);
            if (!visibleNetworks.Contains(expectedSsid, StringComparer.OrdinalIgnoreCase))
            {
                Logger.Warn(Tag, "[NETRELAY-WFD] Connection refused because the expected SSID is not visible");
                return WifiNetworkConnectResult.Failure(
                    savedProfileFound: false,
                    "شبكة Wi-Fi Direct المطلوبة غير ظاهرة في Windows.");
            }

            var savedProfileFound = await HasSavedProfileAsync(expectedSsid, cancellationToken);
            Logger.Info(Tag, $"[NETRELAY-WFD] Saved profile found={savedProfileFound.ToString().ToLowerInvariant()}");

            if (savedProfileFound)
            {
                Logger.Info(Tag, "[NETRELAY-WFD] Using saved WLAN profile");
                var savedProfileState = await ConnectUsingSavedProfileAsync(expectedSsid, cancellationToken);
                if (savedProfileState.IsConnected)
                {
                    LogConnectedState(savedProfileState);
                    return WifiNetworkConnectResult.Success(
                        true,
                        savedProfileState.Ssid,
                        savedProfileState.Ipv4Address);
                }

                Logger.Warn(Tag, "[NETRELAY-WFD] Saved profile authentication failed");
                if (!IsValidWpa2Passphrase(passphrase))
                {
                    return WifiNetworkConnectResult.PasswordNeeded(
                        true,
                        "فشل اتصال Profile المحفوظ. أدخل كلمة المرور الحالية للشبكة مرة واحدة.");
                }

                Logger.Info(Tag, "[NETRELAY-WFD] Updating WLAN profile");
                if (!await InstallOrUpdateProfileAsync(expectedSsid, passphrase!.Trim(), cancellationToken))
                {
                    return WifiNetworkConnectResult.Failure(
                        true,
                        "تعذر تحديث WLAN Profile باستخدام بيانات المجموعة الحالية.");
                }

                Logger.Info(Tag, "[NETRELAY-WFD] Retrying with current group credentials");
                var retryState = await ConnectUsingSavedProfileAsync(expectedSsid, cancellationToken);
                if (retryState.IsConnected)
                {
                    LogConnectedState(retryState);
                    return WifiNetworkConnectResult.Success(
                        true,
                        retryState.Ssid,
                        retryState.Ipv4Address);
                }

                return WifiNetworkConnectResult.Failure(
                    true,
                    "فشل اتصال Windows بالشبكة بعد تحديث WLAN Profile.");
            }

            if (!IsValidWpa2Passphrase(passphrase))
            {
                return WifiNetworkConnectResult.PasswordNeeded(
                    false,
                    "هذه الشبكة غير محفوظة. أدخل كلمة المرور لأول اتصال فقط.");
            }

            Logger.Info(Tag, "[NETRELAY-WFD] Updating WLAN profile");
            if (!await InstallOrUpdateProfileAsync(expectedSsid, passphrase!.Trim(), cancellationToken))
            {
                return WifiNetworkConnectResult.Failure(
                    false,
                    "تعذر إنشاء WLAN Profile للشبكة.");
            }

            var newProfileState = await ConnectUsingSavedProfileAsync(expectedSsid, cancellationToken);
            if (newProfileState.IsConnected)
            {
                LogConnectedState(newProfileState);
                return WifiNetworkConnectResult.Success(
                    true,
                    newProfileState.Ssid,
                    newProfileState.Ipv4Address);
            }

            return WifiNetworkConnectResult.Failure(
                true,
                "فشل اتصال Windows بالشبكة. تحقق من كلمة المرور ومن ظهور الشبكة.");
        }

        public async Task<bool> DisconnectFromNetworkAsync(
            string? expectedSsid,
            CancellationToken cancellationToken = default)
        {
            var currentState = await GetCurrentConnectionStateAsync(expectedSsid ?? string.Empty, cancellationToken);
            Logger.Info(Tag, $"[NETRELAY-WFD] Current Windows SSID={currentState.Ssid ?? "<none>"}");

            var currentSsid = currentState.Ssid;
            var isExpectedNetwork = !string.IsNullOrWhiteSpace(currentSsid) &&
                ((!string.IsNullOrWhiteSpace(expectedSsid) &&
                  string.Equals(currentSsid, expectedSsid.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                 IsNetRelayNetworkName(currentSsid));

            if (!isExpectedNetwork)
            {
                Logger.Info(Tag, "[NETRELAY-WFD] WLAN disconnect skipped; current Wi-Fi is not the NetRelay network");
                return true;
            }

            Logger.Info(Tag, $"[NETRELAY-WFD] Disconnecting Windows from SSID={currentSsid}");
            var disconnect = await RunNetshAsync("wlan disconnect", cancellationToken, allowFailure: true);
            if (disconnect.ExitCode != 0)
            {
                Logger.Warn(Tag, $"[NETRELAY-WFD] WLAN disconnect failed: {disconnect.ErrorOutputOrFallback}");
                return false;
            }

            Logger.Info(Tag, "[NETRELAY-WFD] WLAN disconnected; saved profile retained");
            return true;
        }

        public async Task<bool> HasSavedProfileAsync(
            string networkName,
            CancellationToken cancellationToken = default)
        {
            var profile = await RunNetshAsync(
                $"wlan show profile name=\"{EscapeNetshArgument(networkName)}\"",
                cancellationToken,
                allowFailure: true);
            return profile.ExitCode == 0;
        }

        private async Task<WifiConnectionState> GetCurrentConnectionStateAsync(
            string expectedSsid,
            CancellationToken cancellationToken)
        {
            var result = await RunNetshAsync(
                "wlan show interfaces",
                cancellationToken,
                allowFailure: true);
            return ParseConnectionState(result.StandardOutput, expectedSsid);
        }

        private async Task<WifiConnectionState> ConnectUsingSavedProfileAsync(
            string networkName,
            CancellationToken cancellationToken)
        {
            Logger.Info(Tag, "[NETRELAY-WFD] Connecting...");
            var connect = await RunNetshAsync(
                $"wlan connect name=\"{EscapeNetshArgument(networkName)}\" ssid=\"{EscapeNetshArgument(networkName)}\"",
                cancellationToken,
                allowFailure: true);

            if (connect.ExitCode != 0)
            {
                Logger.Warn(Tag, $"Failed to connect Wi-Fi network: {connect.ErrorOutputOrFallback}");
                return new WifiConnectionState(false, null, null);
            }

            Logger.Info(Tag, $"Windows Wi-Fi connect requested for {networkName}");
            return await WaitForConnectedNetworkAsync(networkName, cancellationToken);
        }

        private async Task<bool> InstallOrUpdateProfileAsync(
            string networkName,
            string passphrase,
            CancellationToken cancellationToken)
        {
            var profilePath = Path.Combine(
                Path.GetTempPath(),
                $"NetRelay-WiFi-{Guid.NewGuid():N}.xml");

            await File.WriteAllTextAsync(
                profilePath,
                BuildWpa2Profile(networkName, passphrase),
                cancellationToken);

            try
            {
                Logger.Info(Tag, "[NETRELAY-WFD] Creating WLAN profile");
                var addProfile = await RunNetshAsync(
                    $"wlan add profile filename=\"{profilePath}\" user=current",
                    cancellationToken);
                if (addProfile.ExitCode != 0)
                {
                    Logger.Warn(Tag, $"[NETRELAY-WFD] Profile creation result=FAILED exitCode={addProfile.ExitCode}");
                    Logger.Warn(Tag, $"Failed to add Wi-Fi profile: {addProfile.ErrorOutputOrFallback}");
                    return false;
                }
                Logger.Info(Tag, "[NETRELAY-WFD] Profile creation result=SUCCESS");
                return true;
            }
            finally
            {
                try
                {
                    File.Delete(profilePath);
                }
                catch (Exception ex)
                {
                    Logger.Debug(Tag, $"Failed to delete temp Wi-Fi profile: {ex.Message}");
                }
            }
        }

        private static void LogConnectedState(WifiConnectionState state)
        {
            Logger.Info(Tag, "[NETRELAY-WFD] WLAN connected");
            Logger.Info(Tag, $"[NETRELAY-WFD] Current SSID={state.Ssid ?? "<none>"}");
            Logger.Info(Tag, $"[NETRELAY-WFD] IPv4 ready={state.Ipv4Address ?? "<none>"}");
        }

        public async Task<bool> TestTcpEndpointAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            Logger.Info(Tag, $"[NETRELAY-WFD] Android P2P endpoint={host}:{port}");
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await client.ConnectAsync(host, port, timeout.Token);
                Logger.Info(Tag, "[NETRELAY-WFD] Endpoint reachable");
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                Logger.Warn(Tag, $"[NETRELAY-WFD] Endpoint unreachable: {ex.Message}");
                return false;
            }
        }

        private async Task<WifiConnectionState> WaitForConnectedNetworkAsync(
            string expectedSsid,
            CancellationToken cancellationToken)
        {
            WifiConnectionState state = new(false, null, null);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var result = await RunNetshAsync(
                    "wlan show interfaces",
                    cancellationToken,
                    allowFailure: true);
                state = ParseConnectionState(result.StandardOutput, expectedSsid);
                if (state.IsConnected)
                {
                    return state;
                }
                await Task.Delay(500, cancellationToken);
            }
            return state;
        }

        private static WifiConnectionState ParseConnectionState(string output, string expectedSsid)
        {
            var stateMatch = Regex.Match(
                output,
                @"^\s*State\s*:\s*(?<state>.+?)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var ssidMatch = Regex.Match(
                output,
                @"^\s*SSID\s*:\s*(?<ssid>.+?)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var stateValue = stateMatch.Success ? stateMatch.Groups["state"].Value.Trim() : string.Empty;
            var ssid = ssidMatch.Success ? ssidMatch.Groups["ssid"].Value.Trim() : null;
            var connected = stateValue.Equals("connected", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ssid, expectedSsid, StringComparison.OrdinalIgnoreCase);

            var ipv4 = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address) &&
                    !address.ToString().StartsWith("169.254.", StringComparison.Ordinal));

            return new WifiConnectionState(connected, ssid, ipv4?.ToString());
        }

        internal static IReadOnlyList<string> ParseNetworkNames(string output)
        {
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => SsidLinePattern.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["ssid"].Value.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? FindExpectedOrRelevantNetwork(
            IEnumerable<string> networkNames,
            string? expectedSsid)
        {
            if (!string.IsNullOrWhiteSpace(expectedSsid))
            {
                var expected = networkNames.FirstOrDefault(name =>
                    string.Equals(name, expectedSsid, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(expected))
                {
                    return expected;
                }
            }

            return networkNames.FirstOrDefault(IsNetRelayNetworkName);
        }

        private static async Task<IReadOnlyList<string>> EnumerateVisibleNetworkNamesAsync(
            CancellationToken cancellationToken)
        {
            var result = await RunNetshAsync(
                "wlan show networks mode=bssid",
                cancellationToken,
                allowFailure: true);
            if (result.ExitCode != 0)
            {
                Logger.Warn(Tag, $"[NETRELAY-WFD] WLAN enumeration failed: {result.ErrorOutputOrFallback}");
                return Array.Empty<string>();
            }

            return ParseNetworkNames(result.StandardOutput);
        }

        private static bool IsValidWpa2Passphrase(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Trim().Length is >= 8 and <= 63 &&
                value.Trim().All(character => character is >= ' ' and <= '~');
        }

        private static bool IsNetRelayNetworkName(string networkName)
        {
            return networkName.StartsWith("DIRECT-NR-", StringComparison.OrdinalIgnoreCase) ||
                networkName.Contains("NetRelay", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildWpa2Profile(string networkName, string passphrase)
        {
            var safeName = SecurityElement.Escape(networkName) ?? networkName;
            var safePassphrase = SecurityElement.Escape(passphrase) ?? passphrase;
            var hexSsid = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(networkName));

            return $"""
                <?xml version="1.0"?>
                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                    <name>{safeName}</name>
                    <SSIDConfig>
                        <SSID>
                            <hex>{hexSsid}</hex>
                            <name>{safeName}</name>
                        </SSID>
                    </SSIDConfig>
                    <connectionType>ESS</connectionType>
                    <connectionMode>manual</connectionMode>
                    <MSM>
                        <security>
                            <authEncryption>
                                <authentication>WPA2PSK</authentication>
                                <encryption>AES</encryption>
                                <useOneX>false</useOneX>
                            </authEncryption>
                            <sharedKey>
                                <keyType>passPhrase</keyType>
                                <protected>false</protected>
                                <keyMaterial>{safePassphrase}</keyMaterial>
                            </sharedKey>
                        </security>
                    </MSM>
                </WLANProfile>
                """;
        }

        private static string EscapeNetshArgument(string value)
        {
            return value.Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        private static async Task<ProcessResult> RunNetshAsync(
            string arguments,
            CancellationToken cancellationToken,
            bool allowFailure = false)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var result = new ProcessResult(
                process.ExitCode,
                await standardOutputTask,
                await standardErrorTask);

            if (!allowFailure && result.ExitCode != 0)
            {
                Logger.Warn(Tag, $"netsh failed: {arguments} :: {result.ErrorOutputOrFallback}");
            }

            return result;
        }

        private sealed record ProcessResult(
            int ExitCode,
            string StandardOutput,
            string StandardError)
        {
            public string ErrorOutputOrFallback =>
                string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        }

        private sealed record WifiConnectionState(
            bool IsConnected,
            string? Ssid,
            string? Ipv4Address);
    }

    public sealed record WifiNetworkConnectResult(
        bool Connected,
        bool PasswordRequired,
        bool SavedProfileFound,
        string? Ssid,
        string? Ipv4Address,
        string? ErrorMessage)
    {
        public static WifiNetworkConnectResult Success(
            bool savedProfileFound,
            string? ssid,
            string? ipv4Address) =>
            new(true, false, savedProfileFound, ssid, ipv4Address, null);

        public static WifiNetworkConnectResult PasswordNeeded(
            bool savedProfileFound,
            string message) =>
            new(false, true, savedProfileFound, null, null, message);

        public static WifiNetworkConnectResult Failure(
            bool savedProfileFound,
            string message) =>
            new(false, false, savedProfileFound, null, null, message);
    }
}
