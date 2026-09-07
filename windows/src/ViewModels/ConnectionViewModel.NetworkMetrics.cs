using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NetRelay.ViewModels
{
    public partial class ConnectionViewModel
    {
        private const int NetworkMetricHistoryLimit = 32;
        private const double NetworkTrendWidth = 260;
        private const double NetworkTrendHeight = 62;

        private static readonly string[] NetRelayAdapterNames = { "NetRelay", "NetRelay Broadband Connection" };

        private readonly Queue<double> _downloadSpeedHistory = new();
        private readonly Queue<double> _uploadSpeedHistory = new();
        private CancellationTokenSource? _networkMetricsCancellation;
        private long _lastReceivedBytes;
        private long _lastSentBytes;
        private long _networkBaselineReceivedBytes;
        private long _networkBaselineSentBytes;
        private DateTimeOffset _lastNetworkSampleAt;
        private bool _metricsSessionActive;
        private DateTimeOffset? _metricsSessionStartedAt;
        private double _downloadBitsPerSecond;
        private double _uploadBitsPerSecond;
        private long _sessionReceivedBytes;
        private long _sessionSentBytes;
        private string _downloadSpeedText = "0 bps";
        private string _uploadSpeedText = "0 bps";
        private string _pingText = "-- ms";
        private string _downloadTotalText = "0 MB";
        private string _uploadTotalText = "0 MB";
        private string _downloadTrendGeometry = BuildFlatTrendGeometry();
        private string _uploadTrendGeometry = BuildFlatTrendGeometry();

        public string DownloadSpeedText
        {
            get => _downloadSpeedText;
            private set => SetProperty(ref _downloadSpeedText, value);
        }

        public string UploadSpeedText
        {
            get => _uploadSpeedText;
            private set => SetProperty(ref _uploadSpeedText, value);
        }

        public string PingText
        {
            get => _pingText;
            private set => SetProperty(ref _pingText, value);
        }

        public string DownloadTotalText
        {
            get => _downloadTotalText;
            private set => SetProperty(ref _downloadTotalText, value);
        }

        public string UploadTotalText
        {
            get => _uploadTotalText;
            private set => SetProperty(ref _uploadTotalText, value);
        }

        public string DownloadTrendGeometry
        {
            get => _downloadTrendGeometry;
            private set => SetProperty(ref _downloadTrendGeometry, value);
        }

        public string UploadTrendGeometry
        {
            get => _uploadTrendGeometry;
            private set => SetProperty(ref _uploadTrendGeometry, value);
        }

        public double DownloadBitsPerSecond => _downloadBitsPerSecond;
        public double UploadBitsPerSecond => _uploadBitsPerSecond;
        public long SessionReceivedBytes => _sessionReceivedBytes;
        public long SessionSentBytes => _sessionSentBytes;
        public DateTimeOffset? MetricsSessionStartedAt => _metricsSessionStartedAt;
        public int ActiveTcpFlows => _dataPlaneService.FlowMetrics.ActiveTcp;
        public int ActiveUdpFlows => _dataPlaneService.FlowMetrics.ActiveUdp;
        public long TotalTcpFlows => _dataPlaneService.FlowMetrics.TotalTcp;
        public long TotalUdpFlows => _dataPlaneService.FlowMetrics.TotalUdp;

        private void StartNetworkMetrics()
        {
            StopNetworkMetrics();
            CaptureNetRelayTotals(out _lastReceivedBytes, out _lastSentBytes);
            _networkBaselineReceivedBytes = _lastReceivedBytes;
            _networkBaselineSentBytes = _lastSentBytes;
            _lastNetworkSampleAt = DateTimeOffset.UtcNow;
            _metricsSessionActive = false;
            _metricsSessionStartedAt = null;
            _downloadSpeedHistory.Clear();
            _uploadSpeedHistory.Clear();
            _networkMetricsCancellation = new CancellationTokenSource();
            ApplyNetworkMetricsOnUiThread(0, 0, null, 0, 0);
            _ = NetworkMetricsLoopAsync(_networkMetricsCancellation.Token);
        }

        private void StopNetworkMetrics()
        {
            _networkMetricsCancellation?.Cancel();
            _networkMetricsCancellation?.Dispose();
            _networkMetricsCancellation = null;
        }

        private async Task NetworkMetricsLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                    CaptureNetRelayTotals(out var receivedBytes, out var sentBytes);
                    var now = DateTimeOffset.UtcNow;
                    var activeSession = IsConnected && IsAdapterModeRunning;
                    if (activeSession && !_metricsSessionActive)
                    {
                        _metricsSessionActive = true;
                        _metricsSessionStartedAt = now;
                        _networkBaselineReceivedBytes = receivedBytes;
                        _networkBaselineSentBytes = sentBytes;
                        _lastReceivedBytes = receivedBytes;
                        _lastSentBytes = sentBytes;
                        _lastNetworkSampleAt = now;
                    }
                    else if (!activeSession)
                    {
                        if (_metricsSessionActive)
                        {
                            _metricsSessionActive = false;
                            _metricsSessionStartedAt = null;
                        }
                        ApplyNetworkMetricsOnUiThread(0, 0, "-- ms", _sessionReceivedBytes, _sessionSentBytes);
                        _lastReceivedBytes = receivedBytes;
                        _lastSentBytes = sentBytes;
                        _lastNetworkSampleAt = now;
                        continue;
                    }
                    var elapsedSeconds = Math.Max(0.001, (now - _lastNetworkSampleAt).TotalSeconds);
                    var receivedDelta = Math.Max(0, receivedBytes - _lastReceivedBytes);
                    var sentDelta = Math.Max(0, sentBytes - _lastSentBytes);
                    var downloadBytesPerSecond = receivedDelta / elapsedSeconds;
                    var uploadBytesPerSecond = sentDelta / elapsedSeconds;
                    var receivedSinceStart = Math.Max(0, receivedBytes - _networkBaselineReceivedBytes);
                    var sentSinceStart = Math.Max(0, sentBytes - _networkBaselineSentBytes);

                    _lastReceivedBytes = receivedBytes;
                    _lastSentBytes = sentBytes;
                    _lastNetworkSampleAt = now;

                    ApplyNetworkMetricsOnUiThread(
                        downloadBytesPerSecond,
                        uploadBytesPerSecond,
                        "-- ms",
                        receivedSinceStart,
                        sentSinceStart);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Debug(Tag, "Network metrics stopped");
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Network metrics error: {ex.Message}", ex);
            }
        }

        private void ApplyNetworkMetricsOnUiThread(
            double downloadBytesPerSecond,
            double uploadBytesPerSecond,
            string? pingText,
            long receivedSinceStart,
            long sentSinceStart)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ApplyNetworkMetrics(
                    downloadBytesPerSecond,
                    uploadBytesPerSecond,
                    pingText,
                    receivedSinceStart,
                    sentSinceStart);
                return;
            }

            _ = dispatcher.BeginInvoke(new Action(() =>
                ApplyNetworkMetrics(
                    downloadBytesPerSecond,
                    uploadBytesPerSecond,
                    pingText,
                    receivedSinceStart,
                    sentSinceStart)));
        }

        private void ApplyNetworkMetrics(
            double downloadBytesPerSecond,
            double uploadBytesPerSecond,
            string? pingText,
            long receivedSinceStart,
            long sentSinceStart)
        {
            PushNetworkMetricSample(_downloadSpeedHistory, downloadBytesPerSecond);
            PushNetworkMetricSample(_uploadSpeedHistory, uploadBytesPerSecond);

            DownloadSpeedText = FormatBitsPerSecond(downloadBytesPerSecond * 8);
            UploadSpeedText = FormatBitsPerSecond(uploadBytesPerSecond * 8);
            _downloadBitsPerSecond = Math.Max(0, downloadBytesPerSecond * 8);
            _uploadBitsPerSecond = Math.Max(0, uploadBytesPerSecond * 8);
            _sessionReceivedBytes = Math.Max(0, receivedSinceStart);
            _sessionSentBytes = Math.Max(0, sentSinceStart);
            DownloadTotalText = FormatBytes(receivedSinceStart);
            UploadTotalText = FormatBytes(sentSinceStart);
            DownloadTrendGeometry = BuildTrendGeometry(_downloadSpeedHistory);
            UploadTrendGeometry = BuildTrendGeometry(_uploadSpeedHistory);

            if (!string.IsNullOrWhiteSpace(pingText))
            {
                PingText = pingText;
            }
        }

        private static void CaptureNetRelayTotals(out long receivedBytes, out long sentBytes)
        {
            receivedBytes = 0;
            sentBytes = 0;

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    !NetRelayAdapterNames.Contains(networkInterface.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var statistics = networkInterface.GetIPv4Statistics();
                    receivedBytes += statistics.BytesReceived;
                    sentBytes += statistics.BytesSent;
                }
                catch
                {
                    // Some virtual adapters can disappear while Windows is refreshing them.
                }
            }
        }

        private static void PushNetworkMetricSample(Queue<double> history, double value)
        {
            history.Enqueue(Math.Max(0, value));
            while (history.Count > NetworkMetricHistoryLimit)
            {
                history.Dequeue();
            }
        }

        private static string FormatBitsPerSecond(double bitsPerSecond)
        {
            if (bitsPerSecond >= 1_000_000_000)
            {
                return $"{bitsPerSecond / 1_000_000_000:0.00} Gbps";
            }
            if (bitsPerSecond >= 1_000_000)
            {
                return $"{bitsPerSecond / 1_000_000:0.0} Mbps";
            }

            if (bitsPerSecond >= 1_000)
            {
                return $"{bitsPerSecond / 1_000:0} Kbps";
            }

            return $"{bitsPerSecond:0} bps";
        }

        private static string FormatBytes(long bytes)
        {
            const double kib = 1024;
            const double mib = kib * 1024;
            const double gib = mib * 1024;

            if (bytes >= gib)
            {
                return $"{bytes / gib:0.00} GB";
            }

            if (bytes >= mib)
            {
                return $"{bytes / mib:0.00} MB";
            }

            if (bytes >= kib)
            {
                return $"{bytes / kib:0.0} KB";
            }

            return $"{bytes} B";
        }

        private static string BuildTrendGeometry(Queue<double> history)
        {
            if (history.Count < 2)
            {
                return BuildFlatTrendGeometry();
            }

            var samples = history.ToArray();
            var max = Math.Max(1, samples.Max());
            var step = NetworkTrendWidth / Math.Max(1, samples.Length - 1);
            var builder = new StringBuilder();

            for (var i = 0; i < samples.Length; i++)
            {
                var x = i * step;
                var normalized = samples[i] / max;
                var y = NetworkTrendHeight - 4 - (normalized * (NetworkTrendHeight - 10));

                builder.Append(i == 0 ? "M " : " L ");
                builder.Append(x.ToString("0.##", CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(y.ToString("0.##", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string BuildFlatTrendGeometry()
        {
            return FormattableString.Invariant($"M 0 {NetworkTrendHeight - 4:0.##} L {NetworkTrendWidth:0.##} {NetworkTrendHeight - 4:0.##}");
        }
    }
}
