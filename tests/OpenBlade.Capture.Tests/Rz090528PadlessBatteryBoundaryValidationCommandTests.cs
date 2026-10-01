using System.Runtime.ExceptionServices;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528PadlessBatteryBoundaryValidationCommandTests
{
    [Fact]
    public void ExactTargetAttendedBoundaryPlanAndLocalJsonAreMandatory()
    {
        string[] args = Arguments();
        Assert.True(Rz090528PadlessBatteryBoundaryValidationCommand.ValidArguments(args));
        foreach (int index in new[] { 1, 3, 5 })
        {
            args = Arguments(); args[index] = "not-reviewed";
            Assert.False(Rz090528PadlessBatteryBoundaryValidationCommand.ValidArguments(args));
        }
        foreach (string path in new[] { "sensors.json", @"\\remote\share\sensors.json", Path.Combine(Path.GetTempPath(), "sensors.csv") })
        {
            args = Arguments(); args[9] = path;
            Assert.False(Rz090528PadlessBatteryBoundaryValidationCommand.ValidArguments(args));
        }
        args = Arguments(); args[7] = Path.Combine(Path.GetTempPath(), "other.exe");
        Assert.False(Rz090528PadlessBatteryBoundaryValidationCommand.ValidArguments(args));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(null, false, null)]
    [InlineData(true, null, null)]
    public void PowerBracketNeverInfersTransitionOrUnknownState(bool? before, bool? after, bool? expected) =>
        Assert.Equal(expected, Rz090528PadlessBatteryBoundaryValidationCommand.CoherentWindowsPower(before, after));

    [Fact]
    public void NoWorkloadPolicyRejectsBenchmarkUiAndChildrenWithoutStoppingThem()
    {
        Assert.True(Rz090528PadlessBatteryBoundaryValidationCommand.NoBenchmarkProcesses(["HWiNFO64", "explorer"]));
        foreach (string name in new[] { "3DMark", "3DMarkCmd", "3DMarkSteelNomad", "3dmarkSteelNomadLight" })
        {
            Assert.False(Rz090528PadlessBatteryBoundaryValidationCommand.NoBenchmarkProcesses(["explorer", name]));
        }
    }

    [Fact]
    public void TelemetryPreservesHotPeakBusyGpuUnknownPowerAndMissingEvidence()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var cpu = new HwinfoCpuLogObservation(now, now, 60, 0, "digest", null, false, 80, now, now);
        var gpu = new StockGpuTelemetry(now, now, true, 11, 65, new Dictionary<string, int>(), null);
        var value = Rz090528PadlessBatteryBoundaryValidationCommand.ComposeReading(cpu, gpu, true, false, false, false, now);
        Assert.Equal(80, value.CpuCelsius);
        Assert.Equal(11u, value.GpuUtilizationPercent);
        Assert.Null(value.WindowsAcOnline);
        Assert.False(value.NoWorkloadConfirmed);
        Assert.False(value.GpuZeroConfirmed);
        Assert.NotNull(Rz090528PadlessBatteryBoundaryValidationCommand.ComposeReading(cpu, gpu, true, true, true, true, now.AddMilliseconds(4001)).Failure);
        Assert.NotNull(Rz090528PadlessBatteryBoundaryValidationCommand.ComposeReading(cpu, gpu with { ExactDeviceConfirmed = false }, true, true, true, true, now).Failure);
        Assert.Equal("NativeFailed", Rz090528PadlessBatteryBoundaryValidationCommand.ComposeReading(cpu, gpu with { Failure = "NativeFailed" }, true, true, true, true, now).Failure);
    }

    [Fact]
    public async Task MissingGpuTelemetryPreservesWindowsPowerAndDoesNotCloseOwnedLane()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var cpu = new HwinfoCpuLogObservation(now, now, 60, 0, "digest", null, false, null, null, null);
        var unavailable = new StockGpuTelemetry(now, now, true, null, null, new Dictionary<string, int>(), "NvmlTemperatureUnavailableOrInvalid");
        var lane = new OwnedReadOnlyLane<Rz090528BatteryBoundaryExternal>(() => { });
        var value = await lane.InvokeAsync(() => Rz090528PadlessBatteryBoundaryValidationCommand.ComposeReading(
            cpu, unavailable, false, false, true, false, now), null, CancellationToken.None);
        Assert.False(value.WindowsAcOnline);
        Assert.Null(value.GpuCelsius);
        Assert.False(value.GpuZeroConfirmed);
        Assert.NotNull(value.Failure);
        var recovered = await lane.InvokeAsync(() => Rz090528PadlessBatteryBoundaryValidationCommand.ComposeReading(
            cpu, unavailable with { GpuCelsius = 45, GpuUtilizationPercent = 0, Failure = null }, true, true, true, true, now), null, CancellationToken.None);
        Assert.True(recovered.WindowsAcOnline);
        Assert.Null(recovered.Failure);
        Assert.True(await lane.CloseAsync());
    }

    [Fact]
    public async Task InvalidArgumentsReturnBeforeAnyNativeAccess()
    {
        using var output = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(2, await Rz090528PadlessBatteryBoundaryValidationCommand.RunWindowsAsync([], output, progress, CancellationToken.None));
        Assert.Empty(progress.ToString());
        Assert.Contains("Exact target", output.ToString());
    }

    [Fact]
    public void UnknownCpuTimestampStillProducesReadableFailureReport()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var cpu = new HwinfoCpuLogObservation(null, now, null, null, "digest", "SourceLost", false, null, null, null);
        var gpu = new StockGpuTelemetry(now, now, true, 0, 45, new Dictionary<string, int>(), null);
        var value = Rz090528PadlessBatteryBoundaryValidationCommand.ComposeReading(cpu, gpu, true, true, true, true, now);
        string report = Rz090528PadlessBatteryBoundaryValidationCommand.SerializeReport(new { Reading = value, Success = false });
        using var parsed = System.Text.Json.JsonDocument.Parse(report);
        Assert.False(parsed.RootElement.GetProperty("Success").GetBoolean());
        Assert.Equal("SourceLost", parsed.RootElement.GetProperty("Reading").GetProperty("Failure").GetString());
        Assert.Equal("NaN", parsed.RootElement.GetProperty("Reading").GetProperty("CpuSourceAgeMilliseconds").GetString());
    }

    [Fact]
    public async Task FinalProbeNeverStartsAlongsideAnUnquiescedExternalRead()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new OwnedReadOnlyLane<string>(() => drained.SetResult(), TimeSpan.FromMilliseconds(100));
        int probes = 0;
        Task<string> blocked = lane.InvokeAsync(() =>
        {
            entered.SetResult(); release.Wait(); return "late";
        }, null, CancellationToken.None).AsTask();
        await WithBlockedLaneCleanupAsync(lane, release, blocked, drained.Task, expectedOperationTimeout: true, async () =>
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await Rz090528PadlessBatteryBoundaryValidationCommand.VerifyFinalGpuZeroAsync(lane,
                () => { probes++; return "zero"; }, value => value == "zero", true, true, CancellationToken.None));
            await Assert.ThrowsAsync<TimeoutException>(() => blocked);
            Assert.False(await Rz090528PadlessBatteryBoundaryValidationCommand.VerifyFinalGpuZeroAsync(lane,
                () => { probes++; return "zero"; }, value => value == "zero", true, true, CancellationToken.None));
            Assert.Equal(0, probes);
            Assert.False(lane.OperationQuiesced);
        });
    }

    [Fact]
    public async Task FinalProbeTimeoutRetainsOwnedResourcesAndReturnsUnconfirmed()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposed = 0;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new OwnedReadOnlyLane<string>(() => { disposed++; drained.SetResult(); }, TimeSpan.FromMilliseconds(100));
        Task<bool> probe = Rz090528PadlessBatteryBoundaryValidationCommand.VerifyFinalGpuZeroAsync(lane,
            () => { entered.SetResult(); release.Wait(); return "zero"; }, value => value == "zero", true, true,
            CancellationToken.None).AsTask();
        await WithBlockedLaneCleanupAsync(lane, release, probe, drained.Task, expectedOperationTimeout: false, async () =>
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await probe.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(lane.OperationQuiesced);
            Assert.False(await lane.CloseAsync());
            Assert.Equal(0, disposed);
        });
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task FailedAssertionStillDrainsActualLaneBeforeEventDisposal()
    {
        using var release = new ManualResetEventSlim();
        var handle = release.WaitHandle.SafeWaitHandle;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new OwnedReadOnlyLane<string>(() => drained.SetResult(), TimeSpan.FromMilliseconds(100));
        Task<string> operation = lane.InvokeAsync(() =>
        {
            release.Wait(); entered.SetResult(); return "late";
        }, null, CancellationToken.None).AsTask();
        var original = new InvalidOperationException("synthetic assertion while worker is blocked");
        Exception? failure = await Record.ExceptionAsync(() => WithBlockedLaneCleanupAsync(
            lane, release, operation, drained.Task, expectedOperationTimeout: true, () =>
            {
                Assert.False(entered.Task.IsCompleted);
                return Task.FromException(original);
            }));
        Assert.Same(original, failure);
        Assert.True(entered.Task.IsCompletedSuccessfully);
        Assert.True(drained.Task.IsCompletedSuccessfully);
        Assert.True(operation.IsCompleted);
        Assert.True(lane.OperationQuiesced);
        Assert.False(handle.IsClosed);
        release.Dispose();
        Assert.True(handle.IsClosed);
    }

    private static async Task WithBlockedLaneCleanupAsync(OwnedReadOnlyLane<string> lane,
        ManualResetEventSlim release, Task operation, Task drained, bool expectedOperationTimeout, Func<Task> assertions)
    {
        Exception? original = null;
        List<Exception> cleanupFailures = [];
        try { await assertions(); }
        catch (Exception error) { original = error; }
        finally
        {
            release.Set();
            try { await operation; }
            catch (TimeoutException) when (expectedOperationTimeout) { }
            catch (Exception error) { cleanupFailures.Add(error); }
            // CloseAsync alone can time out while the real queued read still
            // owns release. Its disposal callback proves the read has returned.
            // Await that same lane, then confirm actual worker termination.
            await lane.CloseAsync();
            await drained;
            try { Assert.True(await lane.CloseAsync(), "The released test lane did not close cleanly."); }
            catch (Exception error) { cleanupFailures.Add(error); }
        }

        if (original is not null)
        {
            cleanupFailures.RemoveAll(error => ReferenceEquals(error, original));
            if (cleanupFailures.Count == 0) { ExceptionDispatchInfo.Capture(original).Throw(); }
            cleanupFailures.Insert(0, original);
        }
        if (cleanupFailures.Count != 0) { throw new AggregateException(cleanupFailures); }
    }

    [Fact]
    public void UsbCVariantRequiresItsOwnConfirmationsAndPlanAndRejectsBatteryArguments()
    {
        var usbC = Rz090528PadlessBatteryBoundaryValidationCommand.UsbC;
        Assert.True(Rz090528PadlessBatteryBoundaryValidationCommand.ValidArguments(Arguments(usbC), usbC));
        Assert.False(Rz090528PadlessBatteryBoundaryValidationCommand.ValidArguments(Arguments(), usbC));
        Assert.False(Rz090528PadlessBatteryBoundaryValidationCommand.ValidArguments(Arguments(usbC)));
        Assert.Equal("0711", usbC.Destination.Power);
        Assert.True(usbC.Destination.WindowsAcOnline);
        Assert.NotEqual(Rz090528PadlessBatteryBoundaryValidationCommand.Battery.DestinationMarker, usbC.DestinationMarker);
        Assert.Equal(Rz090528PadlessBatteryBoundaryValidationCommand.Battery.Destination,
            Rz090528PowerBoundaryDestination.Battery);
    }

    private static string[] Arguments(Rz090528PadlessBatteryBoundaryValidationCommand.Variant? variant = null) =>
    [
        "--confirm-target", (variant ?? Rz090528PadlessBatteryBoundaryValidationCommand.Battery).TargetConfirmation,
        "--confirm-boundary", (variant ?? Rz090528PadlessBatteryBoundaryValidationCommand.Battery).BoundaryConfirmation,
        "--plan-sha256", (variant ?? Rz090528PadlessBatteryBoundaryValidationCommand.Battery).Destination.PlanSha256,
        "--hwinfo-exe", Path.Combine(Path.GetTempPath(), "HWiNFO64.exe"),
        "--hwinfo-log", Path.Combine(Path.GetTempPath(), "sensors.json"),
    ];
}
