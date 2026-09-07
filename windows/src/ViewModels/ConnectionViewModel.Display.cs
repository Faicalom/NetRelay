using System;

namespace NetRelay.ViewModels
{
    public partial class ConnectionViewModel
    {
        public string SystemStatusLabel
        {
            get
            {
                if (IsConnected)
                {
                    return "النظام متصل";
                }

                if (IsConnecting)
                {
                    return "جاري الاتصال";
                }

                if (IsDiscovering || IsDetectingWifiNetworks)
                {
                    return "جاري البحث";
                }

                return "جاهز للبحث";
            }
        }

        public string HomeStatusTitle
        {
            get
            {
                if (IsConnected)
                {
                    return "متصل";
                }

                if (IsConnecting)
                {
                    return "جاري الاتصال";
                }

                if (IsDiscovering || IsDetectingWifiNetworks)
                {
                    return "جار البحث";
                }

                if (UiState == NetRelay.ViewModels.UiState.Error)
                {
                    return "خطأ";
                }

                return "غير متصل";
            }
        }

        public string HomeStatusSubtitle
        {
            get
            {
                if (IsConnected)
                {
                    return IsAdapterModeRunning
                        ? "الإنترنت يعمل بدون وكيل Windows"
                        : "الهاتف متصل، فعّل المحول عند الحاجة";
                }

                if (IsConnecting)
                {
                    return "يحاول الاتصال بالهاتف";
                }

                if (IsDiscovering || IsDetectingWifiNetworks)
                {
                    return "يبحث عن الهاتف أو شبكة Wi-Fi Direct";
                }

                if (UiState == NetRelay.ViewModels.UiState.Error && !string.IsNullOrWhiteSpace(StatusMessage))
                {
                    return StatusMessage;
                }

                return "الهاتف غير متصل";
            }
        }

        public string PhoneNameText => CurrentDevice?.DeviceName ?? "لا يوجد هاتف";

        public string PhoneStatusText => IsConnected && CurrentDevice != null
            ? "متصل"
            : "غير متصل";

        public string ProxyHomeStatusText => IsConnected &&
            !string.IsNullOrWhiteSpace(GetPreferredDataPlaneEndpoint())
            ? (!string.IsNullOrWhiteSpace(GatewayEndpoint) ? "بوابة أصلية" : "وضع توافق")
            : "غير نشط";

        public string ProxyEndpointDisplay => IsConnected &&
            !string.IsNullOrWhiteSpace(GetPreferredDataPlaneEndpoint())
            ? GetPreferredDataPlaneEndpoint()
            : "لا يوجد";

        public string InternetHomeStatusText
        {
            get
            {
                if (!IsConnected)
                {
                    return "غير متصل";
                }

                return IsAdapterModeRunning ? "جيد" : "عبر البروكسي";
            }
        }

        public string InternetDetailText
        {
            get
            {
                if (!IsConnected)
                {
                    return "لا يوجد اتصال هاتف";
                }

                return IsAdapterModeRunning
                    ? "يعمل بدون وكيل Windows"
                    : "الاتصال يحتاج المحول أو البروكسي";
            }
        }

        public string CurrentTransportDisplay => IsConnected
            ? CurrentTransport ?? "غير محدد"
            : "غير محدد";

        public string TargetEndpointDisplay => IsConnected && !string.IsNullOrWhiteSpace(TargetEndpoint)
            ? TargetEndpoint
            : "لا يوجد";

        public string WifiDirectNetworkDisplay => string.IsNullOrWhiteSpace(WifiDirectNetworkName)
            ? "لا توجد شبكة"
            : WifiDirectNetworkName;

        public string UiStateDisplay => UiState switch
        {
            NetRelay.ViewModels.UiState.Connected => "نشطة",
            NetRelay.ViewModels.UiState.Connecting => "اتصال",
            NetRelay.ViewModels.UiState.Reconnecting => "إعادة اتصال",
            NetRelay.ViewModels.UiState.Disconnected => "مقطوعة",
            NetRelay.ViewModels.UiState.Error => "خطأ",
            _ => "خاملة"
        };

        private void OnDisplayDependenciesChanged(string? propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                RefreshDisplayBindings();
                return;
            }

            if (!IsDisplayDependency(propertyName))
            {
                return;
            }

            RefreshDisplayBindings();
        }

        private static bool IsDisplayDependency(string propertyName)
        {
            return propertyName is
                nameof(UiState) or
                nameof(StatusMessage) or
                nameof(IsConnecting) or
                nameof(IsDiscovering) or
                nameof(IsDetectingWifiNetworks) or
                nameof(CurrentDevice) or
                nameof(CurrentTransport) or
                nameof(TargetEndpoint) or
                nameof(WifiDirectNetworkName) or
                nameof(ProxyEndpoint) or
                nameof(GatewayEndpoint) or
                nameof(ProxyStatus) or
                nameof(IsAdapterModeRunning) or
                nameof(AdapterModeStatus);
        }

        private void RefreshDisplayBindings()
        {
            OnPropertyChanged(nameof(SystemStatusLabel));
            OnPropertyChanged(nameof(HomeStatusTitle));
            OnPropertyChanged(nameof(HomeStatusSubtitle));
            OnPropertyChanged(nameof(PhoneNameText));
            OnPropertyChanged(nameof(PhoneStatusText));
            OnPropertyChanged(nameof(ProxyHomeStatusText));
            OnPropertyChanged(nameof(ProxyEndpointDisplay));
            OnPropertyChanged(nameof(InternetHomeStatusText));
            OnPropertyChanged(nameof(InternetDetailText));
            OnPropertyChanged(nameof(CurrentTransportDisplay));
            OnPropertyChanged(nameof(TargetEndpointDisplay));
            OnPropertyChanged(nameof(WifiDirectNetworkDisplay));
            OnPropertyChanged(nameof(UiStateDisplay));
        }
    }
}
