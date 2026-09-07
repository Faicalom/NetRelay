using System.Threading.Tasks;
using NetRelay.Services;

namespace NetRelay.ViewModels
{
    public partial class ConnectionViewModel
    {
        private string _proxyEndpoint = string.Empty;
        private string _gatewayEndpoint = string.Empty;
        private string _proxyStatus = "غير مفعل";

        public string ProxyEndpoint
        {
            get => _proxyEndpoint;
            set => SetProperty(ref _proxyEndpoint, value);
        }

        public string GatewayEndpoint
        {
            get => _gatewayEndpoint;
            set => SetProperty(ref _gatewayEndpoint, value);
        }

        public string ProxyStatus
        {
            get => _proxyStatus;
            set => SetProperty(ref _proxyStatus, value);
        }

        private string GetPreferredDataPlaneEndpoint()
        {
            return !string.IsNullOrWhiteSpace(GatewayEndpoint)
                ? GatewayEndpoint
                : ProxyEndpoint;
        }

        private async Task PrepareAndApplyProxyAsync()
        {
            if (!await StartAdapterModeForCurrentProxyAsync(updateStatusOnly: true))
            {
                throw new System.InvalidOperationException(
                    "AEL2 data plane or adapter/tun2socks setup did not complete.");
            }
        }

        private Task RestoreWindowsProxyAsync()
        {
            return Task.CompletedTask;
        }

        private Task RestoreWindowsProxyForShutdownAsync()
        {
            return Task.CompletedTask;
        }
    }
}
