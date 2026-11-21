using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The shared NVML path used by RZ09-0581 and RZ09-0528 (and, through the same
// SerializedBladeDevice, RZ09-0529 and RZ09-0530): an uncertain NVML submission
// is held until one driver read resolves it into the real offsets.
public sealed partial class WindowsVerifiedBladeDeviceProviderProductionTests
{
    private static (WindowsVerifiedBladeDeviceProvider Provider, IBladeDevice Device,
        IGpuClockOffsetManagementDevice Managed, FakeGpuClockOffsetController Gpu,
        FixedGpuActivitySource Activity, FakeProductionFactory Factory, ControlCoordinator Coordinator)
        CreateGpuResolutionHarness(bool gpuActive)
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
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
        var activity = new FixedGpuActivitySource(gpuActive);
        var factory = new FakeProductionFactory(capabilities, ports);
        WindowsVerifiedBladeDeviceProvider provider = ReadyProvider(factory, gpu, activity);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        var coordinator = new ControlCoordinator(
            device, scopedStateReader: null, resolveIndeterminatePerformanceWrites: true);
        return (provider, device, (IGpuClockOffsetManagementDevice)device, gpu, activity, factory, coordinator);
    }

    private static DesiredDeviceState GpuDesired(GpuClockTuningSettings? tuning) => new(
        new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
        new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
        new ResolvedSetting<LightingSettings>(LightingSettingsFactory.Off(), SettingValueSource.PowerProfile),
        new DesiredBatteryState(BatteryPolicy.Create(80), true, false))
    {
        GpuClockTuning = tuning,
    };

    private static Exception Uncertain() => new BladeWriteException(
        WriteSubmissionState.Indeterminate,
        "GPU clock tuning failed. Unable to set the GPU memory clock offset.");

    // A partial NVML enable (core applied, memory and restore failed), from
    // managed or unmanaged offsets and with an idle or active dGPU.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UncertainEnable_IsResolvedByOneDriverReadAndJudgedFromRealOffsets(
        bool startManaged,
        bool gpuActive)
    {
        var harness = CreateGpuResolutionHarness(gpuActive);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        if (startManaged)
        {
            Assert.Equal(ControlResultStatus.Applied, (await harness.Coordinator.ApplyPerformanceOutcomeAsync(
                PerformancePreset.Balanced, null,
                new GpuClockTuningSettings(true, new GpuClockOffsetSettings(100, 200)), default)).Result.Status);
        }
        ConfirmedDeviceState before = await harness.Device.ReadStateAsync(default);
        int priorMemory = harness.Gpu.Capabilities!.Current.MemoryMegahertz;
        harness.Gpu.SetFailure = settings =>
        {
            harness.Gpu.SetCurrent(new GpuClockOffsetSettings(settings.CoreMegahertz, priorMemory));
            return Uncertain();
        };
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));

        ScopedApplyOutcome outcome = await harness.Coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced, null, tuning, default);
        harness.Gpu.SetFailure = null;

        var driver = new GpuClockOffsetSettings(200, priorMemory);
        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ConfirmationUnavailable);
        Assert.DoesNotContain("remained active", outcome.Result.Message, StringComparison.Ordinal);
        Assert.Equal(driver, outcome.Readback?.GpuClockOffsets?.Current);
        Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);

        ConfirmedDeviceState after = await harness.Device.ReadStateAsync(default);
        Assert.Equal(driver, after.GpuClockOffsets?.Current);
        foreach (bool gpuOnly in new[] { true, false })
        {
            Assert.Equal(PendingConfirmationResult.Mismatch, WriteConfirmationPolicy.Match(
                DeviceSubsystem.Performance,
                new WriteConfirmationExpectation(GpuDesired(tuning), before, GpuClockTuningOnly: gpuOnly),
                after,
                harness.Managed.GpuClockOffsetManagementState));
        }

        int writes = harness.Gpu.Writes.Count;
        ScopedApplyOutcome retry = await harness.Coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced, null, tuning, default);
        Assert.Equal(ControlResultStatus.Applied, retry.Result.Status);
        Assert.Equal(writes + 1, harness.Gpu.Writes.Count);
        Assert.Equal(tuning.EffectiveOffsets, harness.Gpu.Capabilities!.Current);
    }

    // When the driver cannot be read after the uncertain submission, the write
    // stays pending (never reported as a contradiction or retired). A failed
    // ungated query is retried only while the dGPU is active.
    [Fact]
    public async Task UncertainEnable_UnreadableDriverStaysUnresolvedUntilARead()
    {
        var harness = CreateGpuResolutionHarness(gpuActive: false);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        ConfirmedDeviceState before = await harness.Device.ReadStateAsync(default);
        harness.Gpu.SetFailure = settings =>
        {
            harness.Gpu.SetCurrent(new GpuClockOffsetSettings(settings.CoreMegahertz, 0));
            harness.Gpu.QueryFailure = new InvalidOperationException("NVML is unavailable.");
            return Uncertain();
        };
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));

        ScopedApplyOutcome outcome = await harness.Coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced, null, tuning, default);
        harness.Gpu.SetFailure = null;

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.ConfirmationUnavailable);
        Assert.Null(outcome.Readback?.GpuClockOffsets);
        Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, harness.Managed.GpuClockOffsetManagementState);
        Assert.Equal(PendingConfirmationResult.Unavailable, WriteConfirmationPolicy.Match(
            DeviceSubsystem.Performance,
            new WriteConfirmationExpectation(GpuDesired(tuning), before),
            outcome.Readback!,
            harness.Managed.GpuClockOffsetManagementState));

        harness.Gpu.QueryFailure = null;
        int queries = harness.Gpu.QueryCount;
        Assert.Null((await harness.Device.ReadStateAsync(default)).GpuClockOffsets);
        Assert.Equal(queries, harness.Gpu.QueryCount);
        Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, harness.Managed.GpuClockOffsetManagementState);

        harness.Activity.Active = true;
        Assert.Equal(new GpuClockOffsetSettings(200, 0),
            (await harness.Device.ReadStateAsync(default)).GpuClockOffsets?.Current);
        Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);
    }

    // A KnownNotApplied NVML failure leaves the managed confirmation and does not
    // need a driver read; any other failure drops it for the driver's value.
    [Fact]
    public async Task SetFailure_OnlyAnUncertainOneDropsTheManagedConfirmation()
    {
        var harness = CreateGpuResolutionHarness(gpuActive: false);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        await harness.Device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(100, 200), default);

        harness.Gpu.SetFailure = _ => new BladeWriteException(WriteSubmissionState.KnownNotApplied, "restored");
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            harness.Device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(10, 20), default));
        int queries = harness.Gpu.QueryCount;
        Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);
        Assert.Equal(new GpuClockOffsetSettings(100, 200),
            (await harness.Device.ReadStateAsync(default)).GpuClockOffsets?.Current);
        Assert.Equal(queries, harness.Gpu.QueryCount);

        foreach (Func<Exception> failure in new Func<Exception>[]
        {
            Uncertain,
            () => new InvalidOperationException("unexpected driver failure"),
        })
        {
            harness.Gpu.SetFailure = settings =>
            {
                harness.Gpu.SetCurrent(new GpuClockOffsetSettings(settings.CoreMegahertz, 200));
                return failure();
            };
            await Assert.ThrowsAnyAsync<Exception>(() =>
                harness.Device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(10, 20), default));
            Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain,
                harness.Managed.GpuClockOffsetManagementState);
            // The idle dGPU is queried once; the cached 100/200 is not reported.
            Assert.Equal(new GpuClockOffsetSettings(10, 200),
                (await harness.Device.ReadStateAsync(default)).GpuClockOffsets?.Current);
            Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);
            harness.Gpu.SetFailure = null;
            await harness.Device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(100, 200), default);
        }
    }

    // The resolving read restores exact default evidence when the driver is at its
    // defaults: a reset that landed, or an enable that left a confirmed default.
    [Theory]
    [InlineData("reset-landed")]
    [InlineData("reset-partial")]
    [InlineData("enable-left-confirmed-reset")]
    [InlineData("enable-left-relinquished-default")]
    public async Task UncertainSubmission_DefaultsRestoreTheirResetEvidence(string scenario)
    {
        var harness = CreateGpuResolutionHarness(gpuActive: false);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        GpuClockOffsetManagementState expected;
        GpuClockOffsetSettings? expectedOffsets = null;
        switch (scenario)
        {
            case "reset-landed":
            case "reset-partial":
                await harness.Device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(100, 200), default);
                bool landed = scenario == "reset-landed";
                harness.Gpu.SetFailure = _ =>
                {
                    harness.Gpu.SetCurrent(landed ? GpuClockOffsetSettings.Default : new GpuClockOffsetSettings(0, 200));
                    return Uncertain();
                };
                await Assert.ThrowsAsync<BladeWriteException>(() => harness.Managed.SetGpuClockOffsetsAsync(
                    GpuClockOffsetSettings.Default, retainManagement: false, default));
                expected = landed ? GpuClockOffsetManagementState.ResetConfirmed : GpuClockOffsetManagementState.Managed;
                expectedOffsets = landed ? null : new GpuClockOffsetSettings(0, 200);
                break;
            default:
                await harness.Managed.SetGpuClockOffsetsAsync(
                    GpuClockOffsetSettings.Default, retainManagement: false, default);
                expected = GpuClockOffsetManagementState.ResetConfirmed;
                if (scenario == "enable-left-relinquished-default")
                {
                    await harness.Managed.RelinquishGpuClockOffsetManagementAsync(default);
                    expected = GpuClockOffsetManagementState.RelinquishedDefaultConfirmed;
                }
                harness.Gpu.SetFailure = _ =>
                {
                    harness.Gpu.SetCurrent(GpuClockOffsetSettings.Default);
                    return Uncertain();
                };
                await Assert.ThrowsAsync<BladeWriteException>(() =>
                    harness.Device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(200, 500), default));
                break;
        }
        harness.Gpu.SetFailure = null;
        Assert.Equal(GpuClockOffsetManagementState.SubmissionUncertain, harness.Managed.GpuClockOffsetManagementState);

        ConfirmedDeviceState read = await harness.Device.ReadStateAsync(default);

        Assert.Equal(expected, harness.Managed.GpuClockOffsetManagementState);
        Assert.Equal(expectedOffsets, read.GpuClockOffsets?.Current);
    }

    // An uncertain enable that left the confirmed default reset in place: the
    // device restores the reset evidence, so the GPU-only write is reported as
    // leaving the prior state active rather than as a contradiction.
    [Fact]
    public async Task UncertainGpuOnlyEnable_ThatLeftConfirmedDefaults_ReportsPriorStateActive()
    {
        var harness = CreateGpuResolutionHarness(gpuActive: true);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        Assert.Equal(ControlResultStatus.Applied, (await harness.Coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced, null, GpuClockTuningSettings.Disabled, default)).Result.Status);
        Assert.Equal(GpuClockOffsetManagementState.ResetConfirmed, harness.Managed.GpuClockOffsetManagementState);
        harness.Gpu.SetFailure = _ =>
        {
            harness.Gpu.SetCurrent(GpuClockOffsetSettings.Default);
            return Uncertain();
        };

        ScopedApplyOutcome outcome = await harness.Coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced, null,
            new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)), default);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.False(outcome.WriteAttempted);
        Assert.Contains("remained active", outcome.Result.Message, StringComparison.Ordinal);
        Assert.Equal(GpuClockOffsetManagementState.ResetConfirmed, harness.Managed.GpuClockOffsetManagementState);
    }

    // Without an admitted policy envelope the resolving read publishes nothing,
    // never the raw driver range (here +1000 MHz core, +6000 MHz memory).
    [Fact]
    public async Task UncertainResolution_WithoutAdmittedPolicy_PublishesNoRawRange()
    {
        var harness = CreateGpuResolutionHarness(gpuActive: false);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        DeviceCapabilities admitted = harness.Factory.Capabilities;
        harness.Gpu.SetFailure = settings =>
        {
            harness.Gpu.SetCurrent(new GpuClockOffsetSettings(settings.CoreMegahertz, 0));
            return Uncertain();
        };
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            harness.Device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(200, 500), default));
        harness.Gpu.SetFailure = null;
        harness.Factory.Capabilities = admitted with { GpuOverclock = null };
        int queries = harness.Gpu.QueryCount;

        ConfirmedDeviceState read = await harness.Device.ReadStateAsync(default);

        Assert.Equal(queries + 1, harness.Gpu.QueryCount);
        Assert.Null(read.GpuClockOffsets);
        Assert.Equal(GpuClockOffsetManagementState.Managed, harness.Managed.GpuClockOffsetManagementState);
    }
}
