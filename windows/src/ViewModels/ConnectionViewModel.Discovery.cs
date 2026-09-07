using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetRelay.Models;
using NetRelay.Services;
using NetRelay.Transport.Usb;
using NetRelay.Transport.WiFi;

namespace NetRelay.ViewModels
{
    public partial class ConnectionViewModel
    {
        private const string DefaultWifiDirectEndpoint = "192.168.49.1:8988";

        private readonly WifiHotspotDiscoveryService _wifiHotspotDiscoveryService = new();
        private readonly UsbDeviceDiscoveryService _usbDeviceDiscoveryService = new();

        private void InitializeTransports()
        {
            RegisterTransport("wifiDirect", new WifiDirectTransport());
            RegisterTransport("usb", new UsbTransport());

            TransportOptions.Add(new TransportOption
            {
                Key = "wifiDirect",
                DisplayName = "نقطة اتصال / واي فاي",
                IsImplemented = true,
                Hint = "ابحث عن الهاتف تلقائياً على الشبكة الحالية"
            });
            TransportOptions.Add(new TransportOption
            {
                Key = "usb",
                DisplayName = "USB عبر الكابل",
                IsImplemented = true,
                Hint = "يتطلب USB debugging و ADB"
            });
            SelectedTransportOption = TransportOptions.FirstOrDefault(option => option.Key == "wifiDirect");
            TargetEndpoint = DefaultWifiDirectEndpoint;
        }

        private async Task DiscoverDevicesAsync()
        {
            var option = SelectedTransportOption;
            if (option == null)
            {
                StatusMessage = "اختر وضع الاتصال أولاً";
                return;
            }

            if (string.Equals(option.Key, "wifiDirect", StringComparison.OrdinalIgnoreCase))
            {
                await DiscoverWifiDevicesAsync();
                return;
            }

            if (string.Equals(option.Key, "usb", StringComparison.OrdinalIgnoreCase))
            {
                await DiscoverUsbDevicesAsync();
                return;
            }

            StatusMessage = "وضع البحث التلقائي غير متاح لهذا النقل بعد";
        }

        private async Task DiscoverWifiDevicesAsync()
        {
            await DiscoverWithAsync(
                beginMessage: "جارٍ البحث عن الهاتف على الشبكة الحالية...",
                emptyMessage: "لم يتم العثور على هاتف NetRelay على هذه الشبكة",
                discovery: () => _wifiHotspotDiscoveryService.DiscoverAsync());
        }

        private async Task DiscoverUsbDevicesAsync()
        {
            await DiscoverWithAsync(
                beginMessage: "جارٍ فحص هواتف Android المتصلة عبر USB...",
                emptyMessage: "لم يتم العثور على هاتف Android مفعل فيه ADB عبر USB",
                discovery: () => _usbDeviceDiscoveryService.DiscoverAsync());
        }

        private async Task DiscoverWithAsync(
            string beginMessage,
            string emptyMessage,
            Func<Task<IReadOnlyList<Device>>> discovery)
        {
            IsDiscovering = true;
            DiscoveredDevices.Clear();
            SelectedDiscoveredDevice = null;
            StatusMessage = beginMessage;
            RefreshDiagnostics();

            try
            {
                var devices = await discovery();
                foreach (var device in devices)
                {
                    DiscoveredDevices.Add(device);
                }

                if (DiscoveredDevices.Count == 0)
                {
                    StatusMessage = emptyMessage;
                    return;
                }

                SelectedDiscoveredDevice = DiscoveredDevices[0];
                TargetEndpoint = SelectedDiscoveredDevice.DeviceId;
                ProxyEndpoint = SelectedDiscoveredDevice.ProxyEndpoint;
                ProxyStatus = string.IsNullOrWhiteSpace(ProxyEndpoint)
                    ? "الهاتف لم يرسل عنوان بروكسي بعد"
                    : $"عنوان البروكسي المكتشف: {ProxyEndpoint}";
                StatusMessage = $"تم العثور على {DiscoveredDevices.Count} جهاز";
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Discovery error: {ex.Message}", ex);
                StatusMessage = $"فشل البحث: {ex.Message}";
            }
            finally
            {
                IsDiscovering = false;
                RefreshDiagnostics();
            }
        }

        private static string NormalizeTargetEndpoint(string transportType, string targetEndpoint)
        {
            if (!string.Equals(transportType, "wifiDirect", StringComparison.OrdinalIgnoreCase))
            {
                return targetEndpoint.Trim();
            }

            var value = targetEndpoint.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultWifiDirectEndpoint;
            }

            return value.Contains(':', StringComparison.Ordinal) ? value : $"{value}:8988";
        }
    }
}
