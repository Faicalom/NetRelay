using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NetRelay.Utils;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace NetRelay.Transport.WiFi
{
    public sealed class WifiDirectPeer
    {
        public string DeviceId { get; init; } = string.Empty;
        public string EncodedTarget { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsPaired { get; init; }
    }

    public sealed class WifiDirectPeerConnection : IDisposable
    {
        private readonly WiFiDirectDevice _device;

        public WifiDirectPeerConnection(
            WiFiDirectDevice device,
            string remoteAddress,
            string deviceId)
        {
            _device = device;
            RemoteAddress = remoteAddress;
            DeviceId = deviceId;
        }

        public string RemoteAddress { get; }

        public string DeviceId { get; }

        public void Dispose()
        {
            _device.Dispose();
        }
    }

    public static class WifiDirectWinRtConnector
    {
        public const string TargetPrefix = "wfd:";
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "WifiDirectWinRt";

        public static async Task<IReadOnlyList<WifiDirectPeer>> FindPeersAsync()
        {
            try
            {
                var selector = WiFiDirectDevice.GetDeviceSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                return devices
                    .Where(device => !string.IsNullOrWhiteSpace(device.Id))
                    .Select(device => new WifiDirectPeer
                    {
                        DeviceId = device.Id,
                        EncodedTarget = EncodeTarget(device.Id),
                        Name = string.IsNullOrWhiteSpace(device.Name) ? "Wi-Fi Direct device" : device.Name,
                        IsPaired = device.Pairing?.IsPaired == true
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn(Tag, $"Wi-Fi Direct peer enumeration failed: {ex.Message}", ex);
                return Array.Empty<WifiDirectPeer>();
            }
        }

        public static async Task<WifiDirectPeerConnection> ConnectAsync(string encodedTarget)
        {
            var deviceId = DecodeTarget(encodedTarget);
            Logger.Info(Tag, "Connecting to Wi-Fi Direct peer through WinRT");

            var parameters = new WiFiDirectConnectionParameters
            {
                GroupOwnerIntent = 0
            };

            var device = await WiFiDirectDevice.FromIdAsync(deviceId, parameters);
            if (device == null)
            {
                throw new InvalidOperationException("Windows could not create a Wi-Fi Direct device connection");
            }

            var endpointPair = device.GetConnectionEndpointPairs().FirstOrDefault();
            var remoteAddress = endpointPair?.RemoteHostName?.DisplayName;
            if (string.IsNullOrWhiteSpace(remoteAddress))
            {
                device.Dispose();
                throw new InvalidOperationException("Wi-Fi Direct connected but no remote endpoint address was returned");
            }

            Logger.Info(Tag, $"Wi-Fi Direct remote endpoint is {remoteAddress}");
            return new WifiDirectPeerConnection(device, remoteAddress, deviceId);
        }

        public static bool IsEncodedTarget(string target)
        {
            return target.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string EncodeTarget(string deviceId)
        {
            var bytes = Encoding.UTF8.GetBytes(deviceId);
            return TargetPrefix + Convert.ToBase64String(bytes);
        }

        private static string DecodeTarget(string encodedTarget)
        {
            var payload = encodedTarget.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase)
                ? encodedTarget[TargetPrefix.Length..]
                : encodedTarget;
            var bytes = Convert.FromBase64String(payload);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
