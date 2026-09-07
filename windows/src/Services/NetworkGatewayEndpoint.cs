using System;

namespace NetRelay.Services
{
    /// <summary>
    /// Describes the local SOCKS5 gateway exposed by the NetRelay v2 data plane.
    /// </summary>
    public sealed class NetworkGatewayEndpoint
    {
        public NetworkGatewayEndpoint(Uri uri, NetworkGatewayKind kind)
        {
            Uri = uri;
            Kind = kind;
        }

        public Uri Uri { get; }
        public NetworkGatewayKind Kind { get; }
        public bool IsNative => true;
        public string DisplayName => "SOCKS5 native gateway";

        public static NetworkGatewayEndpoint Parse(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("عنوان بوابة الهاتف غير متاح بعد.");
            }

            var value = endpoint.Trim();
            if (!value.Contains("://", StringComparison.Ordinal))
            {
                value = $"socks5://{value}";
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Port <= 0)
            {
                throw new InvalidOperationException($"عنوان بوابة الهاتف غير صالح: {endpoint}");
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme is "socks" or "socks5" or "socks5h")
            {
                var normalized = new UriBuilder(uri)
                {
                    Scheme = "socks5",
                    Port = uri.Port
                }.Uri;
                return new NetworkGatewayEndpoint(normalized, NetworkGatewayKind.Socks5);
            }

            throw new InvalidOperationException(
                $"بروتوكول البوابة غير مدعوم: {uri.Scheme}. المدعوم هو SOCKS5 فقط.");
        }
    }

    public enum NetworkGatewayKind
    {
        Socks5
    }
}
