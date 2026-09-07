using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetRelay.Models;

namespace NetRelay.Services
{
    /// <summary>
    /// Lists ADB-visible Android devices that can be used for USB mode.
    /// </summary>
    public class UsbDeviceDiscoveryService
    {
        private readonly AdbBridgeService _adbBridgeService = new();

        public async Task<IReadOnlyList<Device>> DiscoverAsync()
        {
            var devices = await _adbBridgeService.GetConnectedDevicesAsync();
            return devices
                .Select(device => new Device
                {
                    DeviceId = device.Serial,
                    DeviceName = device.Model,
                    OsVersion = "Android عبر USB",
                    AppVersion = "NetRelay",
                    IsPaired = true,
                    TransportType = "usb",
                    ProxyEndpoint = string.Empty,
                    GatewayEndpoint = _adbBridgeService.GetForwardedGatewayEndpoint()
                })
                .ToList();
        }
    }
}
