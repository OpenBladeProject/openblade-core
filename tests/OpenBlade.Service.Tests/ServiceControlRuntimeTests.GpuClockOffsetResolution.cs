using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// How a pending GPU half is settled: an uncertain NVML submission waits for a
// driver read and is then judged from the real offsets; a GPU half that NVML
// never changed is retired, and a deferred transition keeps its remaining steps.
public sealed partial class ServiceControlRuntimeTests
{
    private static DeviceCapabilities StepLessGpuCapabilities() => DeviceCapabilities.SafeTargetDefaults() with
    {
        GpuOverclock = new GpuOverclockCapabilities(
            new DeviceNumericRange(-1000, 400, null),
            new DeviceNumericRange(-1000, 3000, null),
            DeviceFeatureAccess.ReadWrite),
    };

    private static GpuClockOffsetCapabilities DriverOffsets(int core, int memory) => new(
        "Test NVIDIA GPU",
        new GpuClockOffsetRange(-1000, 400, core),
        new GpuClockOffsetRange(-1000, 3000, memory));

    // What the real device does after an NVML Set whose result is uncertain (a
    // core write whose memory write and restore failed): it keeps no confirmation
    // and holds the submission as uncertain until a driver read resolves it.
    private static void FailNvmlUncertainly(TestContext context, Action? alsoDo = null) =>
        context.Device.AfterGpuClockSet = () =>
        {
            context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.SubmissionUncertain;
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
            alsoDo?.Invoke();
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "GPU clock tuning failed. Unable to set the GPU memory clock offset.");
        };

    // The device's resolving driver read: OpenBlade owns what the driver holds.
    private static void ResolveDriverRead(TestContext context, int core, int memory)
    {
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Managed;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            GpuClockOffsets = DriverOffsets(core, memory),
        };
    }

    // A power transition whose destination profile enables GPU offsets; the NVML
    // step is uncertain on a device that did not manage the offsets.
    [Fact]
    public async Task FullTransition_UncertainNvmlWaitsForDriverReadAndIsJudgedFromRealOffsets()
    {
        DeviceCapabilities capabilities = StepLessGpuCapabilities();
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.Battery,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Unknown;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));
        profiles = profiles with { PluggedIn = profiles.PluggedIn with { GpuClockTuning = tuning } };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            profiles.Battery.Performance,
            profiles.Battery.Fans,
            ConfirmedBattery(80),
            profiles.Battery.Lighting)
        {
            HyperBoostActive = false,
        };
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None)).Status);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        FailNvmlUncertainly(context);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        context.Device.AfterGpuClockSet = null;
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(1, context.Device.GpuClockSetCalls);

        // The driver cannot be read yet: the uncertainty is kept, never retired.
        for (int i = 0; i < 3; i++)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);

        // The driver read shows the partial write (core applied, memory not).
        ResolveDriverRead(context, 200, 0);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Empty(status.UnconfirmedWrites);
        Assert.Equal(new GpuClockOffsetSettings(200, 0), status.ConfirmedState?.GpuClockOffsets?.Current);
        Assert.Contains(DeviceSubsystem.Performance, status.ExternalChanges);
        Assert.Equal(1, context.Device.GpuClockSetCalls);
    }

    // Scenario B: an explicit GPU-only request whose NVML result is uncertain.
    // With the follow-up read lost, or with the read succeeding while the driver
    // cannot be queried, the write stays pending instead of being reported as an
    // external change or retired.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopedGpuOnly_UncertainNvmlStaysPendingUntilDriverRead(bool followUpReadFails)
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = StepLessGpuCapabilities().GpuOverclock,
        };
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Unknown;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));
        FailNvmlUncertainly(context, () =>
        {
            if (followUpReadFails)
            {
                context.Device.ReadStateFailure = new IOException("Injected lost follow-up read.");
            }
        });

        ControlOperationResult write = await context.Runtime.SetGpuClockTuningAsync(
            tuning, OverrideLifetime.Persistent, static () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, write.Status);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        context.Device.ReadStateFailure = null;
        context.Device.AfterGpuClockSet = null;

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);

        ResolveDriverRead(context, 200, 0);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Empty(status.UnconfirmedWrites);
        Assert.Equal(new GpuClockOffsetSettings(200, 0), status.ConfirmedState?.GpuClockOffsets?.Current);
        Assert.Contains(DeviceSubsystem.Performance, status.ExternalChanges);
        Assert.Equal(1, context.Device.GpuClockSetCalls);
    }

    // The preset of a power transition is acknowledged, its final read is lost
    // and the destination's fans, battery and lighting are deferred behind it. The
    // next observation confirms the preset while the GPU offsets were never sent.
    // The deferred steps still run, after the offsets, through the GPU-only setter.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredPowerTransition_UnconfirmedGpuHalfKeepsRemainingProfileSteps(
        bool destinationGpuTuning)
    {
        DeviceCapabilities capabilities = StepLessGpuCapabilities() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateWritableContext(
            initialPowerSource: PowerSource.Battery,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LightingEffects = capabilities.LightingEffects,
            GpuOverclock = capabilities.GpuOverclock,
        };
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Unknown;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500));
        PowerProfile destination = context.Runtime.GetStatus().Profiles.PluggedIn with
        {
            Fans = FanMode.Maximum,
            FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
            BatteryProtectionEnabled = false,
            Lighting = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 40),
            GpuClockTuning = destinationGpuTuning ? tuning : null,
        };
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            context.Runtime.GetStatus().Profiles with { PluggedIn = destination }, CancellationToken.None);
        Assert.True(stored.Status == ControlOperationStatus.Stored, stored.Message);
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
        Assert.Equal(
            (1, 0, 0, 0, 0),
            (context.Device.PerformanceSetCalls, context.Device.FanSetCalls, context.Device.BatterySetCalls,
                context.Device.LightingSetCalls, context.Device.GpuClockSetCalls));
        Assert.True(context.Runtime.ShouldRunPendingTransitionObservation());

        for (int i = 0; i < 4; i++)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(
            (1, 1, 1, 1, destinationGpuTuning ? 1 : 0),
            (context.Device.PerformanceSetCalls, context.Device.FanSetCalls, context.Device.BatterySetCalls,
                context.Device.LightingSetCalls, context.Device.GpuClockSetCalls));
        Assert.False(context.Runtime.ShouldRunPendingTransitionObservation());
        Assert.Empty(status.UnconfirmedWrites);
        Assert.Empty(status.ExternalChanges);
        Assert.Equal(FanMode.Maximum, status.ConfirmedState?.Fans);
        Assert.Equal(LightingEffect.Static, status.ConfirmedState?.Lighting?.Effect);
        Assert.False(status.ConfirmedState?.Battery?.ProtectionEnabled);
        Assert.Equal(
            destinationGpuTuning ? tuning.EffectiveOffsets : null,
            status.ConfirmedState?.GpuClockOffsets?.Current);
    }

    // A GPU-only write retired without a GPU observation proves nothing about the
    // preset, so an earlier external-change latch stays.
    [Fact]
    public async Task GpuOnlyRetire_LeavesEarlierExternalLatch()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = StepLessGpuCapabilities().GpuOverclock,
        };
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Unknown;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        PerformancePreset current = context.Device.ConfirmedState.Performance!.Value;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = current == PerformancePreset.Silent
                ? PerformancePreset.Balanced
                : PerformancePreset.Silent,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);

        // NVML completes, but the confirmation is lost with the follow-up read.
        context.Device.AfterGpuClockSet = () =>
        {
            context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Unknown;
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
            context.Device.ReadStateFailure = new IOException("Injected lost follow-up read.");
        };
        ControlOperationResult write = await context.Runtime.SetGpuClockTuningAsync(
            new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)),
            OverrideLifetime.Persistent, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Failed, write.Status);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        context.Device.ReadStateFailure = null;
        context.Device.AfterGpuClockSet = null;

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
    }

    // The write that experimental resume confirms is not reported as an external
    // change by the observations that follow.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExperimentalResume_ConfirmedWriteIsNotLaterReportedAsExternal(bool withGpuTuning)
    {
        TestContext context = CreateWritableContext();
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = ExperimentalBladePort.CreateGpuOverclockCapabilities(),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        PerformancePreset current = context.Device.ConfirmedState.Performance!.Value;
        PerformancePreset next = current == PerformancePreset.Silent
            ? PerformancePreset.Balanced
            : PerformancePreset.Silent;
        context.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        context.Device.MutatePerformanceBeforeQueuedFailure = true;
        context.Device.BeforeQueuedPerformanceFailure = () =>
        {
            context.Device.ReadStateFailure = new IOException("Injected lost readback.");
            context.Provider.IsExperimentalWritesPaused = true;
        };
        await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(next, OverrideLifetime.Persistent)
            {
                GpuClockTuning = withGpuTuning
                    ? new GpuClockTuningSettings(true, new GpuClockOffsetSettings(125, 500))
                    : null,
            },
            CancellationToken.None);
        context.Device.ReadStateFailure = null;

        ControlOperationResult resume = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, resume.Status);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(next, context.Runtime.GetStatus().ConfirmedState?.Performance);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    // A change made while controls were paused, with no pending write, is still
    // reported as external after resume.
    [Fact]
    public async Task ExperimentalResume_StillReportsChangesWithoutPendingWrite()
    {
        TestContext context = CreateWritableContext();
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Provider.IsExperimentalWritesPaused = true;
        PerformancePreset current = context.Device.ConfirmedState.Performance!.Value;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = current == PerformancePreset.Silent
                ? PerformancePreset.Balanced
                : PerformancePreset.Silent,
        };

        ControlOperationResult resume = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, resume.Status);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
    }
}
