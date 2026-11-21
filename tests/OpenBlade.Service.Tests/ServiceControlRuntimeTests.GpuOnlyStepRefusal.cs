using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// A GPU-only Performance step leaves the firmware preset alone. When the NVIDIA
// driver refuses it before submission, the other thermal steps still apply, the
// GPU failure is reported, and a continuation chain re-sends the offsets once.
public sealed partial class ServiceControlRuntimeTests
{
    private static BladeWriteException PairedGpuFailure() => new(
        WriteSubmissionState.Indeterminate,
        "The power mode was applied, but GPU clock tuning failed. GPU clock tuning failed and the previous offsets were restored.");

    private static Exception GpuOnlyRefusal(string refusal) => refusal == "CapabilityUnavailable"
        ? new CapabilityUnavailableException("GPU clock tuning is unavailable.")
        : new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "GPU clock tuning failed and the previous offsets were restored.");

    // The driver refuses every offset write and keeps its offsets. The paired
    // destination write reports Indeterminate (the preset landed); the GPU-only
    // setter reports what NvidiaGpuClockOffsetController.Set returns from its
    // readback, KnownNotApplied, or CapabilityUnavailable for an unreadable driver.
    private static void RefuseEveryGpuWrite(
        TestContext context,
        GpuClockOffsetManagementState prior,
        string refusal)
    {
        int performanceCallsAtLastGpuSet = context.Device.PerformanceSetCalls;
        context.Device.AfterGpuClockSet = () =>
        {
            bool paired = context.Device.PerformanceSetCalls != performanceCallsAtLastGpuSet;
            performanceCallsAtLastGpuSet = context.Device.PerformanceSetCalls;
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
            context.Device.GpuClockOffsetManagementState = prior;
            throw paired ? PairedGpuFailure() : GpuOnlyRefusal(refusal);
        };
    }

    private static void AssertDeferredStepsApplied(TestContext context)
    {
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(1, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Maximum, status.ConfirmedState?.Fans);
        Assert.Equal(LightingEffect.Static, status.ConfirmedState?.Lighting?.Effect);
        Assert.False(status.ConfirmedState?.Battery?.ProtectionEnabled);
        Assert.False(context.Runtime.ShouldRunPendingTransitionObservation());
        Assert.Empty(status.UnconfirmedWrites);
    }

    [Theory]
    [InlineData("KnownNotApplied", "ResetConfirmed")]
    [InlineData("KnownNotApplied", "Relinquished")]
    [InlineData("CapabilityUnavailable", "Relinquished")]
    public async Task Continuation_RefusedGpuOnlyResend_StillAppliesTheDeferredSteps(
        string refusal,
        string priorState)
    {
        GpuClockOffsetManagementState prior = Enum.Parse<GpuClockOffsetManagementState>(priorState);
        var time = new ManualTimeProvider();
        TestContext context = await CreateGpuContinuationContextAsync(
            time, new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)));
        context.Device.GpuClockOffsetManagementState = prior;
        RefuseEveryGpuWrite(context, prior, refusal);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal((1, 0, 0, 0, 1), SetterCalls(context));

        time.Advance(ServicePeriodicObservationLoop.ObservationInterval);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        string message = context.Runtime.GetStatus().Message;
        Assert.Contains("GPU clock tuning", message);
        Assert.DoesNotContain("No further hardware writes were attempted", message);
        for (int i = 0; i < 11; i++)
        {
            time.Advance(ServicePeriodicObservationLoop.ObservationInterval);
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        // One paired send and one GPU-only re-send.
        Assert.Equal(2, context.Device.GpuClockSetCalls);
        AssertDeferredStepsApplied(context);
    }

    // USB-C to AC with the same preset: the destination Performance step is
    // GPU-only from the start. Its refusal is reported and the fans still apply.
    [Theory]
    [InlineData("KnownNotApplied")]
    [InlineData("CapabilityUnavailable")]
    public async Task FirstTransition_RefusedGpuOnlyStep_StillAppliesTheOtherThermalSteps(string refusal)
    {
        DeviceCapabilities capabilities = ContinuationGpuCapabilities();
        TestContext context = CreateWritableContext(
            initialPowerSource: PowerSource.UsbC,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LightingEffects = capabilities.LightingEffects,
            GpuOverclock = capabilities.GpuOverclock,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        Assert.Equal(profiles.UsbC.Performance, profiles.PluggedIn.Performance);
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            profiles with
            {
                PluggedIn = profiles.PluggedIn with
                {
                    Fans = FanMode.Maximum,
                    FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
                    BatteryProtectionEnabled = false,
                    Lighting = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 40),
                    GpuClockTuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)),
                },
            },
            CancellationToken.None);
        Assert.True(stored.Status == ControlOperationStatus.Stored, stored.Message);
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Relinquished;
        RefuseEveryGpuWrite(context, GpuClockOffsetManagementState.Relinquished, refusal);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal((0, 1, 1, 1, 1), SetterCalls(context));
        Assert.Contains("GPU clock tuning", context.Runtime.GetStatus().Message);
        AssertDeferredStepsApplied(context);
    }

    // A refused firmware preset step still stops the remaining thermal steps.
    [Fact]
    public async Task FirstTransition_RefusedPresetStep_StillStopsTheFanStep()
    {
        TestContext context = CreateWritableContext(initialPowerSource: PowerSource.Battery);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        Assert.NotEqual(profiles.Battery.Performance, profiles.PluggedIn.Performance);
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            profiles with
            {
                PluggedIn = profiles.PluggedIn with
                {
                    Fans = FanMode.Maximum,
                    FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
                },
            },
            CancellationToken.None);
        Assert.True(stored.Status == ControlOperationStatus.Stored, stored.Message);
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied, "Injected refused preset write."));

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Contains("No further hardware writes were attempted", context.Runtime.GetStatus().Message);
    }

    // The refused re-send counts as the chain's one re-send. A later deferred step
    // that fails uncertainly re-creates the continuation, which does not widen again.
    [Fact]
    public async Task RefusedGpuOnlyResend_ThenRecreatedContinuation_DoesNotResendAgain()
    {
        var time = new ManualTimeProvider();
        TestContext context = await CreateGpuContinuationContextAsync(
            time, new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)));
        GpuClockOffsetManagementState prior = GpuClockOffsetManagementState.Relinquished;
        context.Device.GpuClockOffsetManagementState = prior;
        RefuseEveryGpuWrite(context, prior, "KnownNotApplied");
        bool batteryFailed = false;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Battery && !batteryFailed)
            {
                batteryFailed = true;
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Injected acknowledged battery write with a lost reply.");
            }
        };

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        for (int i = 0; i < 6; i++)
        {
            time.Advance(ServicePeriodicObservationLoop.ObservationInterval);
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        Assert.True(batteryFailed);
        Assert.Equal(2, context.Device.GpuClockSetCalls);
        Assert.Equal(LightingEffect.Static, context.Runtime.GetStatus().ConfirmedState?.Lighting?.Effect);
        Assert.False(context.Runtime.ShouldRunPendingTransitionObservation());
    }

    // The flag survives a re-created continuation after a submitted re-send: the
    // re-send fails uncertainly, a later deferred step fails uncertainly too, and
    // the third continuation in the chain does not send the offsets a third time.
    [Fact]
    public async Task GpuResendFlag_SurvivesARecreatedContinuation()
    {
        var time = new ManualTimeProvider();
        TestContext context = await CreateGpuContinuationContextAsync(
            time, new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)));
        GpuClockOffsetManagementState prior = GpuClockOffsetManagementState.ResetConfirmed;
        context.Device.GpuClockOffsetManagementState = prior;
        context.Device.AfterGpuClockSet = () =>
        {
            context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.SubmissionUncertain;
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "GPU clock tuning failed. Unable to set the GPU core clock offset.");
        };
        context.Device.AfterReadState = () =>
        {
            if (context.Device.GpuClockOffsetManagementState == GpuClockOffsetManagementState.SubmissionUncertain)
            {
                context.Device.GpuClockOffsetManagementState = prior;
            }
        };
        bool batteryFailed = false;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Battery && !batteryFailed)
            {
                batteryFailed = true;
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Injected acknowledged battery write with a lost reply.");
            }
        };

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        for (int i = 0; i < 12; i++)
        {
            time.Advance(ServicePeriodicObservationLoop.ObservationInterval);
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        Assert.True(batteryFailed);
        Assert.Equal(2, context.Device.GpuClockSetCalls);
        Assert.Equal(LightingEffect.Static, context.Runtime.GetStatus().ConfirmedState?.Lighting?.Effect);
    }

    // A power change re-arms the one read-only driver query before the
    // destination relinquishes or writes offsets; an observation alone does not.
    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public async Task PowerChange_RearmsTheGpuResolutionBeforeTheDestination(PowerSource destination)
    {
        DeviceCapabilities capabilities = StepLessGpuCapabilities();
        TestContext context = CreateWritableContext(
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = capabilities.GpuOverclock,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            profiles with
            {
                UsbC = profiles.UsbC with
                {
                    GpuClockTuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(100, 300)),
                },
            },
            CancellationToken.None);
        Assert.True(stored.Status == ControlOperationStatus.Stored, stored.Message);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.DoesNotContain("rearm", context.Device.GpuManagementCalls);
        int mark = context.Device.GpuManagementCalls.Count;

        context.Telemetry.Current = CreateTelemetry(destination);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        List<string> calls = context.Device.GpuManagementCalls.Skip(mark).ToList();
        Assert.Equal("rearm", calls[0]);
        Assert.Single(calls, call => call == "rearm");
        Assert.Contains(destination == PowerSource.Battery ? "relinquish" : "gpuSet", calls);
    }

    // A continuation's GPU-only Performance step under manual fans, on a model
    // whose firmware hides the preset there (direct manual-fan transitions,
    // no preset report under manual fans).
    private async Task<CountingBladeDevice> ApplyContinuationGpuOnlyStepAsync(
        PerformancePreset? initialPerformance,
        FanMode desiredFans)
    {
        DeviceCapabilities capabilities = StepLessGpuCapabilities();
        TestContext context = CreateWritableContext(
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = capabilities.GpuOverclock,
            SupportsDirectManualFanTransitions = true,
            ReportsPerformanceWithManualFans = false,
        };
        Assert.Contains(FanMode.Maximum, context.Device.Capabilities.FanModes);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            HyperBoostActive = false,
            Performance = initialPerformance,
            Fans = FanMode.Maximum,
            GpuClockOffsets = null,
        };
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Relinquished;
        ConfirmedDeviceState initial = context.Device.ConfirmedState;
        var desired = new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(desiredFans, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(initial.Lighting ?? LightingSettingsFactory.Off(), SettingValueSource.PowerProfile),
            new DesiredBatteryState(
                BatteryPolicy.Create(initial.Battery?.ChargeLimitPercent ?? 80),
                initial.Battery?.ProtectionEnabled ?? true,
                false))
        {
            GpuClockTuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)),
            FanControl = FanControlSettings.FromLegacy(desiredFans),
        };

        await new ControlCoordinator(context.Device).ApplyDesiredStateOutcomeAsync(
            desired,
            CancellationToken.None,
            continuationScopeResolver: (_, _) => new ContinuationWriteScope(
                new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance, DeviceSubsystem.Fans },
                new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance }));
        return context.Device;
    }

    // The fans return to Automatic and the preset already matches: the GPU-only
    // step takes the GPU setter, never the direct automatic pair that would write
    // the firmware preset together with the fan handback.
    [Fact]
    public async Task ContinuationGpuOnlyStep_WithFanHandback_NeverTakesTheDirectAutomaticPair()
    {
        CountingBladeDevice device = await ApplyContinuationGpuOnlyStepAsync(
            PerformancePreset.Balanced,
            FanMode.Automatic);

        // The pair would mark the fan handback done without a fan write.
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(1, device.GpuClockSetCalls);
    }

    // The fans stay manual and the firmware reports no preset: the hidden selector
    // still admits the GPU-only step, so the continuation sends the offsets alone.
    [Fact]
    public async Task ContinuationGpuOnlyStep_UnderHiddenPreset_SendsTheOffsetsAlone()
    {
        CountingBladeDevice device = await ApplyContinuationGpuOnlyStepAsync(
            initialPerformance: null,
            FanMode.Maximum);

        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(1, device.GpuClockSetCalls);
    }

    // A GPU-only step refused by the driver leaves the fan step to its own
    // admission. A GPU-only step whose fresh pre-write read failed is a laptop
    // access failure, which still stops the remaining thermal steps.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task GpuOnlyStep_PreWriteReadFailure_StillStopsTheFanStep(bool preWriteRead, int fanWrites)
    {
        DeviceCapabilities capabilities = StepLessGpuCapabilities();
        TestContext context = CreateWritableContext(
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = context.Device.Capabilities with { GpuOverclock = capabilities.GpuOverclock };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            HyperBoostActive = false,
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            GpuClockOffsets = null,
        };
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Relinquished;
        context.Device.AfterGpuClockSet = () =>
        {
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
            context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Relinquished;
            throw preWriteRead
                ? new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    BladeWriteFailurePhase.ThermalPreWriteRead,
                    "Injected pre-write read failure.")
                : new BladeWriteException(WriteSubmissionState.KnownNotApplied, "Injected refused GPU write.");
        };
        ConfirmedDeviceState initial = context.Device.ConfirmedState;
        var desired = new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Maximum, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(initial.Lighting ?? LightingSettingsFactory.Off(), SettingValueSource.PowerProfile),
            new DesiredBatteryState(
                BatteryPolicy.Create(initial.Battery?.ChargeLimitPercent ?? 80),
                initial.Battery?.ProtectionEnabled ?? true,
                false))
        {
            GpuClockTuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)),
            FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
        };

        FullTransitionApplyOutcome outcome =
            await new ControlCoordinator(context.Device).ApplyDesiredStateOutcomeAsync(desired, CancellationToken.None);

        Assert.Equal(1, context.Device.GpuClockSetCalls);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Equal(fanWrites, context.Device.FanSetCalls);
        Assert.Contains(DeviceSubsystem.Performance, outcome.GpuClockOffsetsOnlyTried ?? new HashSet<DeviceSubsystem>());
    }
}
