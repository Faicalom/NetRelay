using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NetRelay.Utils;

namespace NetRelay.Services
{
    internal interface IWindowsWlanActiveScanService
    {
        Task<WlanActiveScanResult> RequestScanAsync(CancellationToken cancellationToken = default);
    }

    internal sealed record WlanActiveScanResult(
        bool Succeeded,
        bool TimedOut,
        int RequestedInterfaces,
        int CompletedInterfaces,
        int FailedInterfaces,
        string? FailureReason);

    internal sealed class WindowsWlanActiveScanService : IWindowsWlanActiveScanService
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "WindowsWlanActiveScan";
        private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(6);

        private readonly object _singleFlightLock = new();
        private readonly Func<CancellationToken, Task<WlanActiveScanResult>> _scanOperation;
        private Task<WlanActiveScanResult>? _activeScanTask;

        public WindowsWlanActiveScanService()
            : this(RunNativeScanAsync)
        {
        }

        internal WindowsWlanActiveScanService(
            Func<CancellationToken, Task<WlanActiveScanResult>> scanOperation)
        {
            _scanOperation = scanOperation ?? throw new ArgumentNullException(nameof(scanOperation));
        }

        public Task<WlanActiveScanResult> RequestScanAsync(
            CancellationToken cancellationToken = default)
        {
            Task<WlanActiveScanResult> scanTask;
            lock (_singleFlightLock)
            {
                if (_activeScanTask == null || _activeScanTask.IsCompleted)
                {
                    _activeScanTask = _scanOperation(cancellationToken);
                }

                scanTask = _activeScanTask;
            }

            return scanTask.WaitAsync(cancellationToken);
        }

        private static async Task<WlanActiveScanResult> RunNativeScanAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var openResult = NativeMethods.WlanOpenHandle(
                NativeMethods.WlanClientVersionLonghorn,
                IntPtr.Zero,
                out _,
                out var rawHandle);
            if (openResult != NativeMethods.ErrorSuccess)
            {
                var reason = $"WlanOpenHandle error={openResult}";
                Logger.Warn(Tag, $"[NETRELAY-WFD] Active WLAN scan failed interface=<none> reason={reason}");
                return new WlanActiveScanResult(false, false, 0, 0, 1, reason);
            }

            using var handle = new SafeWlanHandle(rawHandle);
            IntPtr interfaceListPointer = IntPtr.Zero;
            NativeMethods.WlanNotificationCallback? callback = null;

            try
            {
                var enumResult = NativeMethods.WlanEnumInterfaces(
                    handle,
                    IntPtr.Zero,
                    out interfaceListPointer);
                if (enumResult != NativeMethods.ErrorSuccess)
                {
                    var reason = $"WlanEnumInterfaces error={enumResult}";
                    Logger.Warn(Tag, $"[NETRELAY-WFD] Active WLAN scan failed interface=<none> reason={reason}");
                    return new WlanActiveScanResult(false, false, 0, 0, 1, reason);
                }

                var interfaces = ReadInterfaces(interfaceListPointer)
                    .Where(item => item.State != WlanInterfaceState.NotReady)
                    .ToList();
                if (interfaces.Count == 0)
                {
                    const string reason = "No eligible WLAN interfaces";
                    Logger.Warn(Tag, $"[NETRELAY-WFD] Active WLAN scan failed interface=<none> reason={reason}");
                    return new WlanActiveScanResult(false, false, 0, 0, 0, reason);
                }

                var sync = new object();
                var states = interfaces.ToDictionary(
                    item => item.InterfaceGuid,
                    item => new InterfaceScanState(item.Description));
                var terminalSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                callback = (ref WlanNotificationData notification, IntPtr _) =>
                {
                    try
                    {
                        if ((notification.NotificationSource & NativeMethods.WlanNotificationSourceAcm) == 0)
                        {
                            return;
                        }

                        lock (sync)
                        {
                            if (!states.TryGetValue(notification.InterfaceGuid, out var state) ||
                                state.IsTerminal)
                            {
                                return;
                            }

                            if (notification.NotificationCode == NativeMethods.WlanNotificationAcmScanComplete)
                            {
                                state.Completed = true;
                                Logger.Info(
                                    Tag,
                                    $"[NETRELAY-WFD] Active WLAN scan completed interface={state.Description} durationMs={state.Stopwatch.ElapsedMilliseconds}");
                            }
                            else if (notification.NotificationCode == NativeMethods.WlanNotificationAcmScanFail)
                            {
                                state.Failed = true;
                                state.FailureReason = ReadScanFailureReason(notification);
                                Logger.Warn(
                                    Tag,
                                    $"[NETRELAY-WFD] Active WLAN scan failed interface={state.Description} reason={state.FailureReason}");
                            }
                            else
                            {
                                return;
                            }

                            if (states.Values.All(item => item.IsTerminal))
                            {
                                terminalSignal.TrySetResult();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(Tag, $"[NETRELAY-WFD] WLAN notification handling failed reason={ex.Message}");
                    }
                };

                var registerResult = NativeMethods.WlanRegisterNotification(
                    handle,
                    NativeMethods.WlanNotificationSourceAcm,
                    false,
                    callback,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _);
                if (registerResult != NativeMethods.ErrorSuccess)
                {
                    var reason = $"WlanRegisterNotification error={registerResult}";
                    Logger.Warn(Tag, $"[NETRELAY-WFD] Active WLAN scan failed interface=<all> reason={reason}");
                    return new WlanActiveScanResult(false, false, 0, 0, interfaces.Count, reason);
                }

                foreach (var wlanInterface in interfaces)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Logger.Info(
                        Tag,
                        $"[NETRELAY-WFD] Active WLAN scan requested interface={wlanInterface.Description} guid={wlanInterface.InterfaceGuid}");

                    var interfaceGuid = wlanInterface.InterfaceGuid;
                    states[interfaceGuid].Stopwatch.Start();
                    var scanResult = NativeMethods.WlanScan(
                        handle,
                        ref interfaceGuid,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    if (scanResult == NativeMethods.ErrorSuccess)
                    {
                        continue;
                    }

                    lock (sync)
                    {
                        var state = states[interfaceGuid];
                        state.Failed = true;
                        state.FailureReason = $"WlanScan error={scanResult}";
                        Logger.Warn(
                            Tag,
                            $"[NETRELAY-WFD] Active WLAN scan failed interface={state.Description} reason={state.FailureReason}");
                        if (states.Values.All(item => item.IsTerminal))
                        {
                            terminalSignal.TrySetResult();
                        }
                    }
                }

                var timedOut = false;
                try
                {
                    await terminalSignal.Task.WaitAsync(ScanTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                    Logger.Warn(
                        Tag,
                        $"[NETRELAY-WFD] Active WLAN scan timeout afterMs={(int)ScanTimeout.TotalMilliseconds}");
                }

                lock (sync)
                {
                    var completed = states.Values.Count(item => item.Completed);
                    var failed = states.Values.Count(item => item.Failed);
                    var pending = states.Count - completed - failed;
                    var failureReason = states.Values
                        .Select(item => item.FailureReason)
                        .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
                    if (timedOut && pending > 0)
                    {
                        failureReason ??= "Active WLAN scan timed out";
                    }

                    return new WlanActiveScanResult(
                        completed > 0,
                        timedOut,
                        states.Count,
                        completed,
                        failed + pending,
                        failureReason);
                }
            }
            finally
            {
                if (callback != null && !handle.IsInvalid)
                {
                    _ = NativeMethods.WlanRegisterNotification(
                        handle,
                        NativeMethods.WlanNotificationSourceNone,
                        false,
                        null,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        out _);
                    GC.KeepAlive(callback);
                }

                if (interfaceListPointer != IntPtr.Zero)
                {
                    NativeMethods.WlanFreeMemory(interfaceListPointer);
                }
            }
        }

        private static IReadOnlyList<WlanInterfaceInfo> ReadInterfaces(IntPtr listPointer)
        {
            var count = Marshal.ReadInt32(listPointer);
            var itemPointer = IntPtr.Add(listPointer, sizeof(int) * 2);
            var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var interfaces = new List<WlanInterfaceInfo>(Math.Max(0, count));

            for (var index = 0; index < count; index++)
            {
                interfaces.Add(Marshal.PtrToStructure<WlanInterfaceInfo>(itemPointer));
                itemPointer = IntPtr.Add(itemPointer, itemSize);
            }

            return interfaces;
        }

        private static string ReadScanFailureReason(WlanNotificationData notification)
        {
            if (notification.DataPointer == IntPtr.Zero || notification.DataSize < sizeof(int))
            {
                return "scan-fail notification without reason code";
            }

            return $"reasonCode={unchecked((uint)Marshal.ReadInt32(notification.DataPointer))}";
        }

        private sealed class InterfaceScanState
        {
            public InterfaceScanState(string description)
            {
                Description = string.IsNullOrWhiteSpace(description) ? "<unnamed>" : description;
            }

            public string Description { get; }
            public Stopwatch Stopwatch { get; } = new();
            public bool Completed { get; set; }
            public bool Failed { get; set; }
            public string? FailureReason { get; set; }
            public bool IsTerminal => Completed || Failed;
        }

        private sealed class SafeWlanHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeWlanHandle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle() =>
                NativeMethods.WlanCloseHandle(handle, IntPtr.Zero) == NativeMethods.ErrorSuccess;
        }

        private enum WlanInterfaceState
        {
            NotReady = 0,
            Connected = 1,
            AdHocNetworkFormed = 2,
            Disconnecting = 3,
            Disconnected = 4,
            Associating = 5,
            Discovering = 6,
            Authenticating = 7
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Description;

            public WlanInterfaceState State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WlanNotificationData
        {
            public uint NotificationSource;
            public uint NotificationCode;
            public Guid InterfaceGuid;
            public uint DataSize;
            public IntPtr DataPointer;
        }

        private static class NativeMethods
        {
            public const uint ErrorSuccess = 0;
            public const uint WlanClientVersionLonghorn = 2;
            public const uint WlanNotificationSourceNone = 0;
            public const uint WlanNotificationSourceAcm = 0x00000008;
            public const uint WlanNotificationAcmScanComplete = 7;
            public const uint WlanNotificationAcmScanFail = 8;

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void WlanNotificationCallback(
                ref WlanNotificationData notificationData,
                IntPtr context);

            [DllImport("wlanapi.dll")]
            public static extern uint WlanOpenHandle(
                uint clientVersion,
                IntPtr reserved,
                out uint negotiatedVersion,
                out IntPtr clientHandle);

            [DllImport("wlanapi.dll")]
            public static extern uint WlanCloseHandle(
                IntPtr clientHandle,
                IntPtr reserved);

            [DllImport("wlanapi.dll")]
            public static extern uint WlanEnumInterfaces(
                SafeWlanHandle clientHandle,
                IntPtr reserved,
                out IntPtr interfaceList);

            [DllImport("wlanapi.dll")]
            public static extern uint WlanScan(
                SafeWlanHandle clientHandle,
                ref Guid interfaceGuid,
                IntPtr dot11Ssid,
                IntPtr ieData,
                IntPtr reserved);

            [DllImport("wlanapi.dll")]
            public static extern uint WlanRegisterNotification(
                SafeWlanHandle clientHandle,
                uint notificationSource,
                [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate,
                WlanNotificationCallback? callback,
                IntPtr callbackContext,
                IntPtr reserved,
                out uint previousNotificationSource);

            [DllImport("wlanapi.dll")]
            public static extern void WlanFreeMemory(IntPtr memory);
        }
    }
}
