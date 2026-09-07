using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Models;
using NetRelay.Protocol;
using NetRelay.Transport.WiFi;
using NetRelay.Utils;

namespace NetRelay.Services
{
    /// <summary>
    /// Searches Wi-Fi Direct, hotspot, and local Wi-Fi subnets for an Android device exposing NetRelay.
    /// </summary>
    public class WifiHotspotDiscoveryService
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "WifiHotspotDiscovery";
        private const int DefaultPort = 8988;
        private const int MaxConcurrency = 32;

        private readonly MessageSerializer _serializer = new();

        public async Task<IReadOnlyList<Device>> DiscoverAsync(
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            var priorityCandidates = GetPriorityCandidateAddresses();
            var candidateSubnets = GetCandidateSubnets();
            var results = new ConcurrentBag<Device>();
            var probedAddresses = new HashSet<string>(
                priorityCandidates,
                StringComparer.OrdinalIgnoreCase);

            Logger.Info(
                Tag,
                $"Probing {priorityCandidates.Count} Wi-Fi Direct/hotspot candidate(s) before subnet scan");

            await ProbeAddressesAsync(
                priorityCandidates,
                port,
                timeoutMilliseconds: 900,
                results,
                cancellationToken);

            Logger.Info(Tag, $"Scanning {candidateSubnets.Count} local subnet(s) for NetRelay");
            var subnetCandidates = new List<string>();

            foreach (var subnet in candidateSubnets)
            {
                for (var host = 1; host <= 254; host++)
                {
                    var candidateIp = $"{subnet}.{host}";
                    if (probedAddresses.Add(candidateIp))
                    {
                        subnetCandidates.Add(candidateIp);
                    }
                }
            }

            await ProbeAddressesAsync(
                subnetCandidates,
                port,
                timeoutMilliseconds: 250,
                results,
                cancellationToken);

            var winRtPeers = await WifiDirectWinRtConnector.FindPeersAsync();
            Logger.Info(Tag, $"WinRT returned {winRtPeers.Count} Wi-Fi Direct peer(s)");
            foreach (var peer in winRtPeers)
            {
                results.Add(new Device
                {
                    DeviceId = peer.EncodedTarget,
                    DeviceName = peer.Name,
                    OsVersion = "Wi-Fi Direct",
                    AppVersion = "NetRelay",
                    IsPaired = peer.IsPaired,
                    TransportType = "wifiDirect",
                    ProxyEndpoint = string.Empty
                });
            }

            return results
                .GroupBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task ProbeAddressesAsync(
            IEnumerable<string> ipAddresses,
            int port,
            int timeoutMilliseconds,
            ConcurrentBag<Device> results,
            CancellationToken cancellationToken)
        {
            using var semaphore = new SemaphoreSlim(MaxConcurrency);
            var probeTasks = ipAddresses.Select(async ipAddress =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var device = await ProbeEndpointAsync(
                        ipAddress,
                        port,
                        timeoutMilliseconds,
                        cancellationToken);
                    if (device != null)
                    {
                        results.Add(device);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(probeTasks);
        }

        private async Task<Device?> ProbeEndpointAsync(
            string ipAddress,
            int port,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            using var client = new TcpClient();

            try
            {
                var connectTask = client.ConnectAsync(IPAddress.Parse(ipAddress), port);
                var completed = await Task.WhenAny(
                    connectTask,
                    Task.Delay(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken));

                if (completed != connectTask || !client.Connected)
                {
                    return null;
                }

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);

                var queryMessage = new ProtocolMessage
                {
                    Type = MessageTypes.StatusQuery,
                    SessionId = Guid.NewGuid().ToString(),
                    Payload = new Dictionary<string, string>
                    {
                        ["source"] = "windows-discovery"
                    }
                };

                var payload = _serializer.Serialize(queryMessage);
                await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
                await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                var responseLine = await reader.ReadLineAsync().WaitAsync(
                    TimeSpan.FromMilliseconds(1000),
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    return null;
                }

                var response = _serializer.Deserialize(Encoding.UTF8.GetBytes(responseLine));
                if (!string.Equals(response?.Type, MessageTypes.StatusUpdate, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var payloadMap = response?.Payload;
                if (payloadMap == null)
                {
                    return null;
                }

                var endpoint = GetPayloadValue(payloadMap, "listenerEndpoint");
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    endpoint = $"{ipAddress}:{port}";
                }

                return new Device
                {
                    DeviceId = endpoint,
                    DeviceName = GetPayloadValue(payloadMap, "deviceName")
                        ?? $"هاتف Android ({ipAddress})",
                    OsVersion = GetPayloadValue(payloadMap, "osVersion") ?? "Android",
                    AppVersion = "NetRelay",
                    IsPaired = true,
                    TransportType = "wifiDirect",
                    ProxyEndpoint = GetPayloadValue(payloadMap, "proxyEndpoint") ?? string.Empty,
                    GatewayEndpoint = GetPayloadValue(payloadMap, "gatewayEndpoint")
                        ?? GetPayloadValue(payloadMap, "socksEndpoint")
                        ?? string.Empty
                };
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Debug(Tag, $"Probe skipped for {ipAddress}:{port} - {ex.Message}");
                return null;
            }
        }

        private static List<string> GetPriorityCandidateAddresses()
        {
            var candidates = new List<string>
            {
                "192.168.49.1",
                "192.168.49.2",
                "192.168.50.1",
                "192.168.137.1"
            };

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = networkInterface.GetIPProperties();
                foreach (var gateway in properties.GatewayAddresses)
                {
                    var gatewayAddress = gateway.Address.ToString();
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                        IsPrivateAddress(gatewayAddress))
                    {
                        candidates.Add(gatewayAddress);
                    }
                }

                foreach (var addressInfo in properties.UnicastAddresses)
                {
                    var address = addressInfo.Address.ToString();
                    if (addressInfo.Address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivateAddress(address))
                    {
                        continue;
                    }

                    var octets = address.Split('.');
                    if (octets.Length == 4)
                    {
                        var subnet = $"{octets[0]}.{octets[1]}.{octets[2]}";
                        candidates.Add($"{subnet}.1");
                        candidates.Add($"{subnet}.2");
                        candidates.Add($"{subnet}.254");
                    }
                }
            }

            return candidates
                .Where(address => IPAddress.TryParse(address, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetCandidateSubnets()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface =>
                    networkInterface.OperationalStatus == OperationalStatus.Up &&
                    networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(networkInterface =>
                    networkInterface.GetIPProperties().UnicastAddresses
                        .Where(addressInfo =>
                            addressInfo.Address.AddressFamily == AddressFamily.InterNetwork &&
                            IsPrivateAddress(addressInfo.Address.ToString()))
                        .Select(addressInfo => new
                        {
                            Interface = networkInterface,
                            Address = addressInfo.Address.ToString()
                        }))
                .OrderBy(item => ScoreInterface(item.Interface))
                .Select(item => string.Join('.', item.Address.Split('.').Take(3)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        private static int ScoreInterface(NetworkInterface networkInterface)
        {
            var name = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
            return name switch
            {
                var value when value.Contains("wi-fi direct") || value.Contains("p2p") => 0,
                var value when value.Contains("wi-fi") || value.Contains("wireless") => 1,
                var value when value.Contains("wlan") || value.Contains("pdanet") => 2,
                _ => 5
            };
        }

        private static bool IsPrivateAddress(string address)
        {
            if (address.StartsWith("10.", StringComparison.Ordinal))
            {
                return true;
            }

            if (address.StartsWith("192.168.", StringComparison.Ordinal))
            {
                return true;
            }

            if (!address.StartsWith("172.", StringComparison.Ordinal))
            {
                return false;
            }

            var octets = address.Split('.');
            if (octets.Length < 2 || !int.TryParse(octets[1], out var secondOctet))
            {
                return false;
            }

            return secondOctet is >= 16 and <= 31;
        }

        private static string? GetPayloadValue(
            IReadOnlyDictionary<string, string> payload,
            string key)
        {
            return payload.TryGetValue(key, out var value) ? value : null;
        }
    }
}
