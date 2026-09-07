using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Services
{
    /// <summary>
    /// Finds adb, lists connected devices, and prepares local port forwards for USB mode.
    /// </summary>
    public class AdbBridgeService
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "AdbBridgeService";
        private const string PackageName = "com.netrelay.connectivity";
        private const string BundledApkFileName = "NetRelay-Android-1.0.0-release.apk";
        private const string BundledApkSha256 = "C437CD14E60A6457F321A2C059CD7E413DE585A7109825CD134892FD5B991943";
        private const long BundledVersionCode = 1;
        private static readonly HashSet<string> RefreshedApkSerials = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object RefreshedApkSerialsLock = new();
        private readonly SemaphoreSlim _usbDataTunnelLock = new(1, 1);

        private string? _cachedAdbPath;

        public async Task<IReadOnlyList<AdbConnectedDevice>> GetConnectedDevicesAsync()
        {
            Logger.Info(Tag, "[NETRELAY-USB] Running adb devices -l");
            await EnsureAdbServerAsync();
            var result = await RunAdbAsync("devices -l");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.ErrorOutputOrFallback);
            }

            var devices = new List<AdbConnectedDevice>();
            var blockedDevices = new List<AdbConnectedDevice>();
            var lines = result.StandardOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1);

            foreach (var line in lines)
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2)
                {
                    continue;
                }

                var properties = ParseProperties(tokens.Skip(2));
                var model = properties.TryGetValue("model", out var modelValue)
                    ? modelValue.Replace('_', ' ')
                    : tokens[0];

                var adbDevice = new AdbConnectedDevice
                {
                    Serial = tokens[0],
                    State = tokens[1],
                    Model = model,
                    DeviceName = properties.TryGetValue("device", out var deviceName)
                        ? deviceName
                        : model
                };

                if (string.Equals(tokens[1], "device", StringComparison.OrdinalIgnoreCase))
                {
                    devices.Add(adbDevice);
                }
                else
                {
                    blockedDevices.Add(adbDevice);
                }
            }

            if (devices.Count == 0 && blockedDevices.Count > 0)
            {
                throw BuildBlockedDeviceException(blockedDevices);
            }

            Logger.Info(Tag, $"ADB reports {devices.Count} connected device(s)");
            foreach (var device in devices)
                Logger.Info(Tag, $"[NETRELAY-USB] device serial={device.Serial} state={device.State}");
            return devices;
        }

        public async Task<AdbPreparedBridge> PrepareNetRelayUsbAsync(string? requestedSerial)
        {
            Logger.Info(Tag, "[NETRELAY-USB] Connection start");
            Logger.Info(Tag, "[NETRELAY-USB] PrepareNetRelayUsbAsync entered");
            var devices = await GetConnectedDevicesAsync();
            var device = ResolveTargetDevice(requestedSerial, devices);

            await EnsureNetRelayInstalledAsync(device);
            Logger.Info(Tag, "[NETRELAY-USB] Launching Android USB service");
            await LaunchNetRelayAsync(device.Serial, "usb");
            await ForwardPortAsync(device.Serial, ListenerPort);
            // Port 8990 is now PC-local SOCKS5 and must never be reserved by adb.
            await Task.Delay(1200);

            Logger.Info(Tag, "[NETRELAY-USB] Android USB service ready");
            Logger.Info(Tag, $"Prepared NetRelay USB bridge for {device.Serial}");
            return new AdbPreparedBridge
            {
                Serial = device.Serial,
                DeviceName = device.Model,
                ListenerEndpoint = GetForwardedListenerEndpoint(),
                ProxyEndpoint = string.Empty,
                GatewayEndpoint = GetForwardedGatewayEndpoint()
            };
        }

        public async Task<AdbPreparedPhone> InstallOrUpdateNetRelayAsync(
            string? requestedSerial,
            string transportMode = "usb",
            bool allowSignatureReset = false,
            Action<string>? progress = null)
        {
            progress?.Invoke("checking");
            var devices = await GetConnectedDevicesAsync();
            var device = ResolveTargetDevice(requestedSerial, devices);
            var apkPath = ResolveNetRelayApkPath();
            VerifyBundledApkIntegrity(apkPath);
            var installed = await GetInstalledPackageInfoAsync(device.Serial);

            if (installed != null && installed.VersionCode > BundledVersionCode)
            {
                progress?.Invoke("opening");
                await LaunchNetRelayAsync(device.Serial, transportMode);
                Logger.Info(Tag, $"A newer Android package is installed versionCode={installed.VersionCode}; downgrade skipped");
                return new AdbPreparedPhone
                {
                    Serial = device.Serial,
                    DeviceName = device.Model,
                    ApkPath = apkPath,
                    Outcome = "newerInstalled",
                    InstalledVersionCode = installed.VersionCode,
                    InstalledVersionName = installed.VersionName
                };
            }

            progress?.Invoke(installed == null ? "installing" : "updating");
            try
            {
                await InstallApkAsync(device.Serial, apkPath);
            }
            catch (NetRelayAndroidException ex) when (
                ex.Code == AndroidErrorCodes.SignatureMismatch && allowSignatureReset)
            {
                Logger.Warn(Tag, $"User-confirmed Android reinstall for serial={device.Serial}; local app data will be removed");
                await UninstallPackageAsync(device.Serial);
                await InstallApkAsync(device.Serial, apkPath);
            }

            progress?.Invoke("opening");
            await LaunchNetRelayAsync(device.Serial, transportMode);

            Logger.Info(Tag, $"Installed and launched NetRelay on {device.Serial}");
            return new AdbPreparedPhone
            {
                Serial = device.Serial,
                DeviceName = device.Model,
                ApkPath = apkPath,
                Outcome = installed == null ? "installed" : allowSignatureReset ? "reinstalled" : "updated",
                InstalledVersionCode = BundledVersionCode,
                InstalledVersionName = "1.0.0"
            };
        }

        [Obsolete("Use PrepareNetRelayUsbDataTunnelAsync. Port 8990 is PC-local in NetRelay v2.")]
        public Task<AdbPreparedBridge> PrepareNetRelayUsbGatewayAsync(string? requestedSerial)
        {
            return PrepareNetRelayUsbDataTunnelAsync(requestedSerial);
        }

        public async Task<AdbPreparedBridge> PrepareNetRelayUsbDataTunnelAsync(string? requestedSerial)
        {
            await _usbDataTunnelLock.WaitAsync();
            try
            {
                return await PrepareNetRelayUsbDataTunnelCoreAsync(requestedSerial);
            }
            finally
            {
                _usbDataTunnelLock.Release();
            }
        }

        private async Task<AdbPreparedBridge> PrepareNetRelayUsbDataTunnelCoreAsync(string? requestedSerial)
        {
            Logger.Info(Tag, "[NETRELAY-USB] Preparing USB data tunnel");
            Logger.Info(Tag, "[NETRELAY-USB] PrepareNetRelayUsbDataTunnelAsync entered");
            var devices = await GetConnectedDevicesAsync();
            var device = ResolveTargetDevice(requestedSerial, devices);

            await EnsureNetRelayInstalledAsync(device);
            await ForwardPortAsync(device.Serial, LocalDataTunnelPort, RemoteGatewayPort);
            if (await IsForwardedGatewayReadyAsync())
            {
                Logger.Info(Tag, "[NETRELAY-USB] Android USB service already active; skipping relaunch");
            }
            else
            {
                Logger.Info(Tag, "[NETRELAY-USB] Android USB service not ready; launching once");
                await LaunchNetRelayAsync(device.Serial, "usb");
                await Task.Delay(500);
            }
            // Port 8990 belongs exclusively to the PC-local SOCKS server. Remove any stale
            // ADB mapping before creating the data tunnel to keep endpoint ownership strict.
            Logger.Info(Tag, $"[NETRELAY-USB] Removing stale 8992/8990 forwards for {device.Serial}");
            await RemoveForwardAsync(device.Serial, GatewayPort);
            Logger.Info(Tag, "[NETRELAY-USB] Creating 8992 -> 8991");
            await ForwardPortAsync(device.Serial, LocalDataTunnelPort, RemoteGatewayPort);
            await Task.Delay(350);

            var forwardList = await RunAdbAsync($"-s {device.Serial} forward --list", allowNonZeroExit: true);
            Logger.Info(Tag, $"[NETRELAY-USB] forward list:\n{forwardList.StandardOutput.Trim()}\n{forwardList.StandardError.Trim()}");
            if (!forwardList.StandardOutput.Contains($"tcp:{LocalDataTunnelPort} tcp:{RemoteGatewayPort}", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ADB forward 8992 -> 8991 was not created");

            Logger.Info(
                Tag,
                $"Prepared NetRelay v2 data tunnel tcp:{LocalDataTunnelPort}->tcp:{RemoteGatewayPort} for {device.Serial}");
            return new AdbPreparedBridge
            {
                Serial = device.Serial,
                DeviceName = device.Model,
                ListenerEndpoint = GetForwardedListenerEndpoint(),
                ProxyEndpoint = string.Empty,
                GatewayEndpoint = $"ael2://127.0.0.1:{LocalDataTunnelPort}"
            };
        }

        public async Task CleanupNetRelayUsbAsync(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                return;
            }

            await RemoveForwardAsync(serial, ListenerPort);
            await RemoveForwardAsync(serial, GatewayPort);
            await RemoveForwardAsync(serial, LocalDataTunnelPort);
        }

        public string GetForwardedListenerEndpoint() => $"127.0.0.1:{ListenerPort}";

        public string GetForwardedGatewayEndpoint() => $"socks5://127.0.0.1:{GatewayPort}";

        private async Task EnsureAdbServerAsync()
        {
            var result = await RunAdbAsync("start-server", allowNonZeroExit: false);
            Logger.Debug(Tag, $"adb start-server exited with {result.ExitCode}");
        }

        private async Task ForwardPortAsync(string serial, int port)
        {
            Logger.Info(Tag, $"[NETRELAY-USB] Creating adb forward tcp:{port} tcp:{port}");
            await RunAdbAsync($"-s {serial} forward --remove tcp:{port}", allowNonZeroExit: true);
            var result = await RunAdbAsync($"-s {serial} forward tcp:{port} tcp:{port}");
            Logger.Info(Tag, $"[NETRELAY-USB] forward {port} exit code={result.ExitCode} stdout={result.StandardOutput.Trim()} stderr={result.StandardError.Trim()}");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to forward tcp:{port}: {result.ErrorOutputOrFallback}");
            }
        }

        private async Task ForwardPortAsync(string serial, int localPort, int remotePort)
        {
            await RunAdbAsync($"-s {serial} forward --remove tcp:{localPort}", allowNonZeroExit: true);
            var result = await RunAdbAsync($"-s {serial} forward tcp:{localPort} tcp:{remotePort}");
            Logger.Info(Tag, $"[NETRELAY-USB] forward {localPort}->{remotePort} exit code={result.ExitCode} stdout={result.StandardOutput.Trim()} stderr={result.StandardError.Trim()}");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to forward tcp:{localPort} to tcp:{remotePort}: {result.ErrorOutputOrFallback}");
            }
        }

        private async Task RemoveForwardAsync(string serial, int port)
        {
            await RunAdbAsync($"-s {serial} forward --remove tcp:{port}", allowNonZeroExit: true);
        }

        private async Task EnsureNetRelayInstalledAsync(AdbConnectedDevice device)
        {
            // Normal connection must never reinstall the APK just because the PC app
            // restarted. Installation/update is an explicit action in PreparePhoneAsync.
            if (await IsPackageInstalledAsync(device.Serial))
            {
                return;
            }

            var apkPath = ResolveNetRelayApkPath();
            VerifyBundledApkIntegrity(apkPath);
            await InstallApkAsync(device.Serial, apkPath);
            MarkApkRefreshedForThisRun(device.Serial);
        }

        private async Task<bool> IsPackageInstalledAsync(string serial)
        {
            var result = await RunAdbAsync(
                $"-s {serial} shell pm path {PackageName}",
                allowNonZeroExit: true);

            return result.ExitCode == 0 &&
                result.StandardOutput.TrimStart().StartsWith("package:", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<InstalledPackageInfo?> GetInstalledPackageInfoAsync(string serial)
        {
            if (!await IsPackageInstalledAsync(serial))
            {
                return null;
            }

            var result = await RunAdbAsync(
                $"-s {serial} shell dumpsys package {PackageName}",
                allowNonZeroExit: true);
            if (result.ExitCode != 0)
            {
                return new InstalledPackageInfo { VersionCode = 0, VersionName = string.Empty };
            }

            var versionCodeMatch = Regex.Match(
                result.StandardOutput,
                @"(?m)^\s*versionCode=(\d+)",
                RegexOptions.CultureInvariant);
            var versionNameMatch = Regex.Match(
                result.StandardOutput,
                @"(?m)^\s*versionName=([^\r\n]+)",
                RegexOptions.CultureInvariant);

            return new InstalledPackageInfo
            {
                VersionCode = versionCodeMatch.Success && long.TryParse(versionCodeMatch.Groups[1].Value, out var code)
                    ? code
                    : 0,
                VersionName = versionNameMatch.Success ? versionNameMatch.Groups[1].Value.Trim() : string.Empty
            };
        }

        private async Task InstallApkAsync(string serial, string apkPath)
        {
            var result = await RunAdbAsync(
                $"-s {serial} install -r {QuoteArgument(apkPath)}",
                allowNonZeroExit: true);

            if (result.ExitCode != 0 ||
                !result.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                var details = result.ErrorOutputOrFallback;
                Logger.Error(Tag, $"Android install failed for serial={serial}: {details}");
                if (details.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase) ||
                    details.Contains("signatures do not match", StringComparison.OrdinalIgnoreCase) ||
                    details.Contains("different signature", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NetRelayAndroidException(AndroidErrorCodes.SignatureMismatch, serial);
                }

                throw new NetRelayAndroidException(AndroidErrorCodes.InstallFailed, serial);
            }
        }

        private async Task UninstallPackageAsync(string serial)
        {
            var result = await RunAdbAsync(
                $"-s {serial} uninstall {PackageName}",
                allowNonZeroExit: true);
            if (result.ExitCode != 0 ||
                !result.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error(Tag, $"User-confirmed Android uninstall failed for serial={serial}: {result.ErrorOutputOrFallback}");
                throw new NetRelayAndroidException(AndroidErrorCodes.UninstallFailed, serial);
            }
        }

        private static bool WasApkRefreshedForThisRun(string serial)
        {
            lock (RefreshedApkSerialsLock)
            {
                return RefreshedApkSerials.Contains(serial);
            }
        }

        private static void MarkApkRefreshedForThisRun(string serial)
        {
            lock (RefreshedApkSerialsLock)
            {
                RefreshedApkSerials.Add(serial);
            }
        }

        private async Task LaunchNetRelayAsync(string serial, string transportMode)
        {
            Logger.Info(Tag, "[NETRELAY-USB] Launching Android NetRelay");
            var safeMode = string.IsNullOrWhiteSpace(transportMode) ? "usb" : transportMode.Trim();
            var command =
                $"shell am start -n {PackageName}/.presentation.MainActivity --es transport_mode {safeMode}";
            var result = await RunAdbAsync($"-s {serial} {command}", allowNonZeroExit: true);
            Logger.Info(Tag, $"[NETRELAY-USB] Launch command exit code={result.ExitCode} stdout={result.StandardOutput.Trim()} stderr={result.StandardError.Trim()}");
            if (result.ExitCode != 0)
            {
                Logger.Error(Tag, $"Android launch failed for serial={serial}: {result.ErrorOutputOrFallback}");
                throw new NetRelayAndroidException(AndroidErrorCodes.LaunchFailed, serial);
            }
        }

        private static async Task<bool> IsForwardedGatewayReadyAsync()
        {
            using var probe = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
            try
            {
                await probe.ConnectAsync("127.0.0.1", LocalDataTunnelPort, timeout.Token);
                return probe.Connected;
            }
            catch
            {
                return false;
            }
        }

        private async Task<AdbCommandResult> RunAdbAsync(string arguments, bool allowNonZeroExit = false)
        {
            var adbPath = ResolveAdbPath();
            if (adbPath == null)
            {
                throw new NetRelayAndroidException(AndroidErrorCodes.BundledAdbMissing);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var result = new AdbCommandResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = await standardOutputTask,
                StandardError = await standardErrorTask
            };

            if (!allowNonZeroExit && result.ExitCode != 0)
            {
                Logger.Error(Tag, $"ADB command failed: {arguments} :: {result.ErrorOutputOrFallback}");
            }

            return result;
        }

        private string? ResolveAdbPath()
        {
            if (!string.IsNullOrWhiteSpace(_cachedAdbPath) && File.Exists(_cachedAdbPath))
            {
                return _cachedAdbPath;
            }

            var candidate = Path.Combine(AppContext.BaseDirectory, "tools", "adb", "adb.exe");
            if (File.Exists(candidate))
            {
                _cachedAdbPath = candidate;
                Logger.Info(Tag, $"Using bundled adb at {_cachedAdbPath}");
                return _cachedAdbPath;
            }

            return null;
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

        private static string ResolveNetRelayApkPath()
        {
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "android", BundledApkFileName);
            if (!File.Exists(bundledPath))
            {
                throw new NetRelayAndroidException(AndroidErrorCodes.BundledApkMissing);
            }

            return bundledPath;
        }

        private static void VerifyBundledApkIntegrity(string apkPath)
        {
            using var stream = File.OpenRead(apkPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(BundledApkSha256, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error(Tag, $"Bundled Android APK integrity mismatch expected={BundledApkSha256} actual={actualHash}");
                throw new NetRelayAndroidException(AndroidErrorCodes.IntegrityFailed);
            }

            Logger.Info(Tag, $"Bundled Android APK integrity verified sha256={actualHash}");
        }

        private static string QuoteArgument(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static Dictionary<string, string> ParseProperties(IEnumerable<string> tokens)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                var delimiterIndex = token.IndexOf(':');
                if (delimiterIndex <= 0)
                {
                    continue;
                }

                var key = token[..delimiterIndex];
                var value = token[(delimiterIndex + 1)..];
                values[key] = value;
            }

            return values;
        }

        private static NetRelayAndroidException BuildBlockedDeviceException(IReadOnlyList<AdbConnectedDevice> devices)
        {
            var unauthorized = devices.FirstOrDefault(device =>
                string.Equals(device.State, "unauthorized", StringComparison.OrdinalIgnoreCase));
            if (unauthorized != null)
            {
                return new NetRelayAndroidException(AndroidErrorCodes.Unauthorized, unauthorized.Serial);
            }

            var offline = devices.FirstOrDefault(device =>
                string.Equals(device.State, "offline", StringComparison.OrdinalIgnoreCase));
            if (offline != null)
            {
                return new NetRelayAndroidException(AndroidErrorCodes.Offline, offline.Serial);
            }

            return new NetRelayAndroidException(AndroidErrorCodes.DeviceUnavailable);
        }

        private static AdbConnectedDevice ResolveTargetDevice(
            string? requestedSerial,
            IReadOnlyList<AdbConnectedDevice> devices)
        {
            if (devices.Count == 0)
            {
                throw new NetRelayAndroidException(AndroidErrorCodes.NoDevice);
            }

            if (!string.IsNullOrWhiteSpace(requestedSerial))
            {
                var exact = devices.FirstOrDefault(device =>
                    string.Equals(device.Serial, requestedSerial, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }

                throw new NetRelayAndroidException(AndroidErrorCodes.SelectedDeviceMissing, requestedSerial);
            }

            if (devices.Count == 1)
            {
                return devices[0];
            }

            throw new NetRelayAndroidException(AndroidErrorCodes.MultipleDevices);
        }

        public class AdbConnectedDevice
        {
            public string Serial { get; set; } = string.Empty;
            public string State { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public string DeviceName { get; set; } = string.Empty;
        }

        public class AdbPreparedBridge
        {
            public string Serial { get; set; } = string.Empty;
            public string DeviceName { get; set; } = string.Empty;
            public string ListenerEndpoint { get; set; } = string.Empty;
            public string ProxyEndpoint { get; set; } = string.Empty;
            public string GatewayEndpoint { get; set; } = string.Empty;
        }

        public class AdbPreparedPhone
        {
            public string Serial { get; set; } = string.Empty;
            public string DeviceName { get; set; } = string.Empty;
            public string ApkPath { get; set; } = string.Empty;
            public string Outcome { get; set; } = string.Empty;
            public long InstalledVersionCode { get; set; }
            public string InstalledVersionName { get; set; } = string.Empty;
        }

        private sealed class InstalledPackageInfo
        {
            public long VersionCode { get; set; }
            public string VersionName { get; set; } = string.Empty;
        }

        public sealed class NetRelayAndroidException : InvalidOperationException
        {
            public NetRelayAndroidException(string code, string? serial = null)
                : base(code)
            {
                Code = code;
                Serial = serial;
            }

            public string Code { get; }
            public string? Serial { get; }
        }

        public static class AndroidErrorCodes
        {
            public const string IntegrityFailed = "ANDROID_APK_INTEGRITY_FAILED";
            public const string BundledAdbMissing = "BUNDLED_ADB_MISSING";
            public const string BundledApkMissing = "BUNDLED_APK_MISSING";
            public const string NoDevice = "ANDROID_NO_DEVICE";
            public const string Unauthorized = "ANDROID_UNAUTHORIZED";
            public const string Offline = "ANDROID_OFFLINE";
            public const string DeviceUnavailable = "ANDROID_DEVICE_UNAVAILABLE";
            public const string MultipleDevices = "ANDROID_MULTIPLE_DEVICES";
            public const string SelectedDeviceMissing = "ANDROID_SELECTED_DEVICE_MISSING";
            public const string SignatureMismatch = "ANDROID_SIGNATURE_MISMATCH";
            public const string InstallFailed = "ANDROID_INSTALL_FAILED";
            public const string UninstallFailed = "ANDROID_UNINSTALL_FAILED";
            public const string LaunchFailed = "ANDROID_LAUNCH_FAILED";
        }

        private class AdbCommandResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; } = string.Empty;
            public string StandardError { get; set; } = string.Empty;

            public string ErrorOutputOrFallback =>
                string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        }

        private const int ListenerPort = 8988;
        private const int GatewayPort = Socks5GatewayServer.ListenPort; // PC-local SOCKS5; never adb-forward this in v2
        public const int RemoteGatewayPort = 8991; // Android AEL2 framed gateway
        public const int LocalDataTunnelPort = 8992; // PC raw tunnel endpoint; SOCKS5 stays local on 8990
    }
}
