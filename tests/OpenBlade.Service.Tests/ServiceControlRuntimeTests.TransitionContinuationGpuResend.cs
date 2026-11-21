using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// A deferred power transition may send an unconfirmed GPU half again, once, and
// only through the GPU-only setter; its own re-send is not an outside change.
public sealed partial class ServiceControlRuntimeTests
{
    private static DeviceCapabilities ContinuationGpuCapabilities() => StepLessGpuCapabilities() with
    {
        LightingEffects = new HashSet<LightingEffect>
        {
            LightingEffect.Off,
            LightingEffect.Static,
        },
    };

    private static (int Performance, int Fans, int Battery, int Lighting, int Gpu) SetterCalls(TestContext context) =>
        (context.Device.PerformanceSetCalls, context.Device.FanSetCalls, context.Device.BatterySetCalls,
            context.Device.LightingSetCalls, context.Device.GpuClockSetCalls);

    private async Task<TestContext> CreateGpuContinuationContextAsync(
        ManualTimeProvider? time,
        GpuClockTuningSettings? destinationTuning,
        FanMode destinationFans = FanMode.Maximum)
    {
        DeviceCapabilities capabilities = ContinuationGpuCapabilities();
        TestContext context = CreateWritableContext(
            initialPowerSource: PowerSource.Battery,
            timeProvider: time,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LightingEffects = capabilities.LightingEffects,
            GpuOverclock = capabilities.GpuOverclock,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        PowerProfile destination = context.Runtime.GetStatus().Profiles.PluggedIn with
        {
            Fans = destinationFans,
            FanControl = FanControlSettings.FromLegacy(destinationFans),
            BatteryProtectionEnabled = false,
            Lighting = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 40),
            GpuClockTuning = destinationTuning,
        };
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            context.Runtime.GetStatus().Profiles with { PluggedIn = destination }, CancellationToken.None);
        Assert.True(stored.Status == ControlOperationStatus.Stored, stored.Message);
        return context;
    }

    // Every NVML write fails uncertainly while the driver keeps its confirmed
    // defaults, so each resolving read restores the default evidence and leaves
    // the GPU half unconfirmed. The chain re-sends it once; the deferred fan,
    // battery and lighting steps then apply and nothing loops.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continuation_GpuResendKeepsFailing_ResendsOnceThenAppliesDeferredSteps(
        bool relinquishedDefault)
    {
        GpuClockOffsetManagementState priorDefaultState = relinquishedDefault
            ? GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
            : GpuClockOffsetManagementState.ResetConfirmed;
        var time = new ManualTimeProvider();
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));
        TestContext context = await CreateGpuContinuationContextAsync(time, tuning);
        context.Device.GpuClockOffsetManagementState = priorDefaultState;
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
                context.Device.GpuClockOffsetManagementState = priorDefaultState;
            }
        };

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal((1, 0, 0, 0, 1), SetterCalls(context));

        for (int i = 0; i < 12; i++)
        {
            time.Advance(ServicePeriodicObservationLoop.ObservationInterval);
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal((1, 1, 1, 1, 2), SetterCalls(context));
        Assert.False(context.Runtime.ShouldRunPendingTransitionObservation());
        Assert.Empty(status.UnconfirmedWrites);
        Assert.Equal(FanMode.Maximum, status.ConfirmedState?.Fans);
        Assert.Equal(LightingEffect.Static, status.ConfirmedState?.Lighting?.Effect);
        Assert.False(status.ConfirmedState?.Battery?.ProtectionEnabled);
    }

    // The continuation's GPU-only re-send confirms the offsets. It is published
    // as this transition's own write, even when the session saw different managed
    // offsets earlier, so it is not left latched as an outside change.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Continuation_GpuResendIsNotReportedAsAnOutsideChange(
        bool earlierGpuObservation,
        bool directManualFans)
    {
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));
        TestContext context = await CreateGpuContinuationContextAsync(null, tuning, FanMode.Automatic);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsDirectManualFanTransitions = directManualFans,
        };
        if (earlierGpuObservation)
        {
            context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Managed;
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = DriverOffsets(100, 200) };
            await context.Runtime.ObserveAsync(CancellationToken.None);
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
        }
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Relinquished;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    BladeWriteFailurePhase.ThermalFinalConfirmationRead,
                    "Injected acknowledged performance final-read failure.");
            }
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        context.Device.AfterSetter = null;

        for (int i = 0; i < 3; i++)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(1, context.Device.GpuClockSetCalls);
        Assert.Equal(tuning.EffectiveOffsets, status.ConfirmedState?.GpuClockOffsets?.Current);
        Assert.Empty(status.UnconfirmedWrites);
        Assert.Empty(status.ExternalChanges);
    }

    // RZ09-0529 and RZ09-0530 report the preset under manual fans, but the policy
    // skips it, so a lost destination preset write is judged only from the GPU
    // half. The continuation may then send the offsets through the GPU-only setter
    // when the preset landed; it never falls back to the paired firmware setter.
    // Without GPU tuning the matched dependency is never put back in scope.
    [Theory]
    [InlineData(0x02C7, false, true)]
    [InlineData(0x02C7, true, true)]
    [InlineData(0x02C5, false, true)]
    [InlineData(0x02C7, false, false)]
    [InlineData(0x02C5, false, false)]
    public async Task ManualFans_ContinuationAfterLostPresetWrite_NeverReplaysTheFirmwareSetter(
        int productId,
        bool presetLanded,
        bool gpuTuning)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, true);
        Assert.True(capabilities.ReportsPerformanceWithManualFans);
        Assert.NotNull(capabilities.GpuOverclock);
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.Battery,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.BatterySaver,
            FanMode.Automatic,
            ConfirmedBattery(80),
            context.Runtime.GetStatus().Profiles.Battery.Lighting)
        {
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        var fixedFans = new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default);
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Fixed,
                FanControl = fixedFans,
                BatteryProtectionEnabled = false,
                GpuClockTuning = gpuTuning ? tuning : null,
            },
        };
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        Assert.True(stored.Status == ControlOperationStatus.Stored, stored.Message);
        context.Device.FanTargetsAvailable = true;
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.BatteryBalanced, OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            fixedFans,
            CancellationToken.None)).Status);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        context.Device.MutatePerformanceBeforeQueuedFailure = presetLanded;

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal([PerformancePreset.BatteryBalanced, PerformancePreset.Balanced], context.Device.PerformanceWrites);
        for (int i = 0; i < 3; i++)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal([PerformancePreset.BatteryBalanced, PerformancePreset.Balanced], context.Device.PerformanceWrites);
        Assert.Equal(gpuTuning && presetLanded ? 1 : 0, context.Device.GpuClockSetCalls);
        Assert.Equal(1, context.Device.BatterySetCalls);
        Assert.False(status.ConfirmedState?.Battery?.ProtectionEnabled);
        Assert.Empty(status.UnconfirmedWrites);
    }

    // A lighting change made while paused is still reported after resume; an
    // unattributed Off reading is not evidence of another controller.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExperimentalResume_LightingChangedWhilePaused(bool observedOff)
    {
        DeviceCapabilities lightingCapabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Static },
        };
        TestContext context = CreateWritableContext(
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(lightingCapabilities));
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LightingEffects = lightingCapabilities.LightingEffects,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            HyperBoostActive = false,
            Lighting = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 40),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.DoesNotContain(DeviceSubsystem.Lighting, context.Runtime.GetStatus().ExternalChanges);
        context.Provider.IsExperimentalWritesPaused = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = observedOff
                ? LightingSettingsFactory.Off()
                : LightingSettingsFactory.Static(new RgbColor(200, 0, 0), 90),
        };

        ControlOperationResult resume = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, resume.Status);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(!observedOff, context.Runtime.GetStatus().ExternalChanges.Contains(DeviceSubsystem.Lighting));
    }
}
