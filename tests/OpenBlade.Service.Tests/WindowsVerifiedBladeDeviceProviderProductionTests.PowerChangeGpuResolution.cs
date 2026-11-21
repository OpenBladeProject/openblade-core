using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// A power change after an uncertain NVML submission, on the shared RZ09-0581 and
// RZ09-0528 path: the destination reads the driver once, read-only, before it
// relinquishes or writes offsets, and an unreadable driver keeps the submission
// uncertain instead of releasing or overwriting offsets nothing confirmed.
public sealed partial class WindowsVerifiedBladeDeviceProviderProductionTests
{
    private static async Task<(WindowsVerifiedBladeDeviceProvider Provider, IBladeDevice Device,
            IGpuClockOffsetManagementDevice Managed, FakeGpuClockOffsetController Gpu)>
        CreateUncertainEnableAtIdleGpuAsync(bool memoryLanded)
    {
        var harness = CreateGpuResolutionHarness(gpuActive: false);
        // The enable applies core (and memory, when it landed), then the
        // confirmation fails and the driver stops answering.
        harness.Gpu.SetFailure = settings =>
        {
            harness.Gpu.SetCurrent(new GpuClockOffsetSettings(
                settings.CoreMegahertz, memoryLanded ? settings.MemoryMegahertz : 0));
            harness.Gpu.QueryFailure = new InvalidOperationException("NVML is unavailable.");
            return Uncertain();
        };
        ScopedApplyOutcome outcome = await harness.Coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced, null,
            new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)), default);
        harness.Gpu.SetFailure = null;
        harness.Gpu.QueryFailure = null;
        Assert.True(outcome.ConfirmationUnavailable);
        Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, harness.Managed.GpuClockOffsetManagementState);
        // The ungated query was spent on the failed confirmation; an idle read
        // does not query again.
        int queries = harness.Gpu.QueryCount;
        Assert.Null((await harness.Device.ReadStateAsync(default)).GpuClockOffsets);
        Assert.Equal(queries, harness.Gpu.QueryCount);
        return (harness.Provider, harness.Device, harness.Managed, harness.Gpu);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("same", false)]
    [InlineData("same", true)]
    [InlineData("different", false)]
    public async Task PowerChangeDestination_ReadsAnUncertainSubmissionBeforeAnyOffsetWrite(
        string destinationTuning,
        bool memoryLanded)
    {
        var harness = await CreateUncertainEnableAtIdleGpuAsync(memoryLanded);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        GpuClockTuningSettings? destination = destinationTuning switch
        {
            "same" => new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)),
            "different" => new GpuClockTuningSettings(true, new GpuClockOffsetSettings(100, 300)),
            _ => null,
        };
        int mark = harness.Gpu.Operations.Count;

        harness.Managed.RearmUncertainGpuClockOffsetResolution();
        FullTransitionApplyOutcome transition = await new ControlCoordinator(harness.Device)
            .ApplyDesiredStateOutcomeAsync(GpuDesired(destination), default);

        List<string> operations = harness.Gpu.Operations.Skip(mark).ToList();
        Assert.Equal("query", operations[0]);
        GpuClockOffsetSettings driver = harness.Gpu.Capabilities!.Current;
        if (destination is null)
        {
            // Resolved as managed 200/0, then released without touching the driver.
            Assert.Equal(["query"], operations);
            Assert.Equal(GpuClockOffsetManagementState.Relinquished, harness.Managed.GpuClockOffsetManagementState);
            Assert.Equal(new GpuClockOffsetSettings(200, 0), driver);
            return;
        }

        Assert.Equal(destination.EffectiveOffsets, driver);
        Assert.Equal(destination.EffectiveOffsets, transition.Readback?.GpuClockOffsets?.Current);
        Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);
        if (memoryLanded)
        {
            // The read shows the requested offsets already applied: nothing to write.
            Assert.Equal(["query"], operations);
            Assert.DoesNotContain(DeviceSubsystem.Performance, transition.WriteAttempted);
        }
        else
        {
            Assert.Single(operations, operation => operation == "set");
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("same")]
    public async Task PowerChangeDestination_UnreadableDriverKeepsTheSubmissionUncertain(string destinationTuning)
    {
        var harness = await CreateUncertainEnableAtIdleGpuAsync(memoryLanded: false);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        GpuClockOffsetCapabilities? driver = harness.Gpu.Capabilities;
        // The real controller reports an unavailable driver as no reading.
        harness.Gpu.Capabilities = null;
        int mark = harness.Gpu.Operations.Count;

        harness.Managed.RearmUncertainGpuClockOffsetResolution();
        FullTransitionApplyOutcome transition = await new ControlCoordinator(harness.Device)
            .ApplyDesiredStateOutcomeAsync(
                GpuDesired(destinationTuning == "same"
                    ? new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500))
                    : null),
                default);

        List<string> operations = harness.Gpu.Operations.Skip(mark).ToList();
        Assert.NotEmpty(operations);
        Assert.All(operations, operation => Assert.Equal("query", operation));
        Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, harness.Managed.GpuClockOffsetManagementState);
        Assert.Null(transition.Readback?.GpuClockOffsets);
        if (destinationTuning == "none")
        {
            Assert.Equal(["query"], operations);
        }
        else
        {
            Assert.NotEqual(ControlResultStatus.Applied, transition.Results.Performance.Status);
        }

        // The driver answers later, while the dGPU is active: the submission is
        // judged from what it reports.
        harness.Gpu.Capabilities = driver;
        harness.Managed.RearmUncertainGpuClockOffsetResolution();
        Assert.Equal(
            new GpuClockOffsetSettings(200, 0),
            (await harness.Device.ReadStateAsync(default)).GpuClockOffsets?.Current);
        Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);
    }

    // Without a power change an idle relinquish has no query left, so it keeps
    // the submission uncertain instead of releasing unconfirmed offsets.
    [Fact]
    public async Task Relinquish_WithoutAQueryLeft_KeepsTheSubmissionUncertain()
    {
        var harness = await CreateUncertainEnableAtIdleGpuAsync(memoryLanded: false);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        int mark = harness.Gpu.Operations.Count;

        await harness.Managed.RelinquishGpuClockOffsetManagementAsync(default);

        Assert.Equal(mark, harness.Gpu.Operations.Count);
        Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, harness.Managed.GpuClockOffsetManagementState);
    }

    // An offset write reads the driver before it submits. That read resolves an
    // uncertain submission even when the write is then refused, so a later idle
    // read does not wait for GPU activity to learn what the driver reported.
    [Fact]
    public async Task OffsetWrite_PreflightReadResolvesTheSubmissionBeforeARefusal()
    {
        var harness = await CreateUncertainEnableAtIdleGpuAsync(memoryLanded: false);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        harness.Gpu.SetFailure = _ => new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "NVML write refused.");

        await Assert.ThrowsAsync<BladeWriteException>(() => harness.Managed.SetGpuClockOffsetsAsync(
            new GpuClockOffsetSettings(100, 300),
            retainManagement: true,
            default));
        harness.Gpu.SetFailure = null;

        Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);
        Assert.Equal(
            new GpuClockOffsetSettings(200, 0),
            (await harness.Device.ReadStateAsync(default)).GpuClockOffsets?.Current);
    }

    // A firmware-only preset write whose ACK was lost is judged from the preset
    // alone, whether or not an earlier NVML submission is still unresolved. The
    // unchanged readback shows the prior preset in place: nothing is left pending.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostFirmwareOnlyAck_IsNotBlamedOnAnUnresolvedGpuSubmission(bool retainedUncertain)
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Silent },
            new HashSet<FanMode> { FanMode.Automatic });
        FakeProductionPort[] ports = Enumerable.Range(0, 60)
            .Select(_ => new FakeProductionPort(
                capabilities,
                BatteryValidationSetting.Limit80,
                temporary: null,
                performanceFan: new ProductionPerformanceFanState(
                    PerformancePreset.Balanced,
                    FanMode.Automatic)))
            .ToArray();
        var gpu = new FakeGpuClockOffsetController();
        var factory = new FakeProductionFactory(capabilities, ports);
        using WindowsVerifiedBladeDeviceProvider provider =
            ReadyProvider(factory, gpu, new FixedGpuActivitySource(false));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        var managed = (IGpuClockOffsetManagementDevice)device;
        var coordinator = new ControlCoordinator(
            device, scopedStateReader: null, resolveIndeterminatePerformanceWrites: true);
        if (retainedUncertain)
        {
            // An uncertain enable, then a power change to a destination without
            // tuning that cannot read the driver: the submission stays uncertain.
            gpu.SetFailure = settings =>
            {
                gpu.SetCurrent(new GpuClockOffsetSettings(settings.CoreMegahertz, 0));
                gpu.QueryFailure = new InvalidOperationException("NVML is unavailable.");
                return Uncertain();
            };
            _ = await coordinator.ApplyPerformanceOutcomeAsync(
                PerformancePreset.Balanced, null,
                new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)), default);
            gpu.SetFailure = null;
            gpu.QueryFailure = null;
            gpu.Capabilities = null;
            managed.RearmUncertainGpuClockOffsetResolution();
            _ = await new ControlCoordinator(device).ApplyDesiredStateOutcomeAsync(GpuDesired(null), default);
            Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, managed.GpuClockOffsetManagementState);
        }
        foreach (FakeProductionPort port in ports)
        {
            port.WriteFailure = new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK.");
        }
        int mark = gpu.Operations.Count;

        ScopedApplyOutcome lost = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Silent, null, null, default);

        Assert.Contains("prior performance state remained active", lost.Result.Message);
        Assert.DoesNotContain("GPU clock offsets", lost.Result.Message);
        Assert.False(lost.WriteAttempted);
        Assert.False(lost.ConfirmationUnavailable);
        Assert.Equal(mark, gpu.Operations.Count);
        if (retainedUncertain)
        {
            Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, managed.GpuClockOffsetManagementState);
        }
    }
}
