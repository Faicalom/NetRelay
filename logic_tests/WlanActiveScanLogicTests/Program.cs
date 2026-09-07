using NetRelay.Services;

if (args.Contains("--native", StringComparer.OrdinalIgnoreCase))
{
    var nativeResult = await new WindowsWlanActiveScanService().RequestScanAsync();
    Console.WriteLine(
        $"NATIVE_SCAN succeeded={nativeResult.Succeeded} timedOut={nativeResult.TimedOut} " +
        $"requested={nativeResult.RequestedInterfaces} completed={nativeResult.CompletedInterfaces} " +
        $"failed={nativeResult.FailedInterfaces} reason={nativeResult.FailureReason ?? "<none>"}");
    Environment.ExitCode = nativeResult.Succeeded ? 0 : 1;
    return;
}

static void Assert(string name, bool condition, string details)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{name}=FAIL: {details}");
    }

    Console.WriteLine($"{name}=PASS: {details}");
}

static WlanActiveScanResult ScanSuccess() => new(true, false, 1, 1, 0, null);
static WlanActiveScanResult ScanFailure(string reason = "mock failure") =>
    new(false, false, 1, 0, 1, reason);
static WlanActiveScanResult ScanTimeout() =>
    new(false, true, 1, 0, 1, "mock timeout");

async Task TestSingleFlightAsync()
{
    var calls = 0;
    var release = new TaskCompletionSource<WlanActiveScanResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var activeScan = new WindowsWlanActiveScanService(_ =>
    {
        Interlocked.Increment(ref calls);
        return release.Task;
    });

    var first = activeScan.RequestScanAsync();
    var second = activeScan.RequestScanAsync();
    await Task.Delay(25);
    Assert("SINGLE_FLIGHT_CALL_COUNT", calls == 1, "two callers shared one native scan operation");
    release.SetResult(ScanSuccess());
    var results = await Task.WhenAll(first, second);
    Assert("SINGLE_FLIGHT_RESULT", results.All(result => result.Succeeded), "both callers received completion");
}

async Task TestCompletionAndDelayedPublicationAsync()
{
    var enumerationCalls = 0;
    var activeScan = new WindowsWlanActiveScanService(_ => Task.FromResult(ScanSuccess()));
    var service = new WifiNetworkScanService(
        activeScan,
        _ => Task.FromResult<IReadOnlyList<string>>(
            Interlocked.Increment(ref enumerationCalls) == 1
                ? new[] { "4GMIFI" }
                : new[] { "4GMIFI", "DIRECT-NR-M5-NetRelay" }),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(10));

    var networks = await service.GetVisibleNetworkNamesAsync(
        requestActiveScan: true);
    Assert("SCAN_COMPLETION", networks.Contains("DIRECT-NR-M5-NetRelay"), "fresh result was returned");
    Assert("DELAYED_PUBLICATION", enumerationCalls == 2, "enumeration retried after scan completion");
}

async Task TestFailureAndTimeoutFallbackAsync()
{
    foreach (var test in new[]
    {
        (Name: "FAILURE_FALLBACK", Result: ScanFailure()),
        (Name: "TIMEOUT_FALLBACK", Result: ScanTimeout())
    })
    {
        var calls = 0;
        var activeScan = new WindowsWlanActiveScanService(_ => Task.FromResult(test.Result));
        var service = new WifiNetworkScanService(
            activeScan,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<IReadOnlyList<string>>(new[] { "CachedNetwork" });
            },
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10));

        var networks = await service.GetVisibleNetworkNamesAsync(requestActiveScan: true);
        Assert(test.Name, calls == 1 && networks.SequenceEqual(new[] { "CachedNetwork" }),
            "cached enumeration was used once without throwing");
    }
}

async Task TestCancellationAsync()
{
    var activeScan = new WindowsWlanActiveScanService(async token =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return ScanSuccess();
    });
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    var canceled = false;
    try
    {
        await activeScan.RequestScanAsync(cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        canceled = true;
    }

    Assert("SCAN_CANCELLATION", canceled, "cancellation propagated without a leaked wait");
}

void TestSelectionAndDeduplication()
{
    var parsed = WifiNetworkScanService.ParseNetworkNames(
        "SSID 1 : DIRECT-NR-A\r\nSSID 2 : direct-nr-a\r\nSSID 3 : Other\r\n");
    Assert("SSID_DEDUPLICATION", parsed.Count == 2, "SSID parsing remains case-insensitively deduplicated");

    var service = new WifiNetworkScanService();
    var preserved = service.SelectBestNetRelayNetwork(
        new[] { "DIRECT-NR-NEW", "DIRECT-NR-CURRENT" },
        "direct-nr-current");
    Assert("SELECTED_SSID_PRESERVATION", preserved == "DIRECT-NR-CURRENT",
        "the currently selected visible SSID remains selected");

    var replacement = service.SelectBestNetRelayNetwork(
        new[] { "DIRECT-NR-NEW" },
        "DIRECT-NR-MISSING");
    Assert("MISSING_SSID_REPLACEMENT", replacement == "DIRECT-NR-NEW",
        "a missing selection is replaced truthfully");
}

await TestSingleFlightAsync();
await TestCompletionAndDelayedPublicationAsync();
await TestFailureAndTimeoutFallbackAsync();
await TestCancellationAsync();
TestSelectionAndDeduplication();
Console.WriteLine("WLAN_ACTIVE_SCAN_LOGIC_TESTS=PASS");
