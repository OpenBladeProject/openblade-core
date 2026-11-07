using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task AutomaticFanStopRequiresOptInAndDefaultThirtySecondInterval()
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync();
        await SampleAutomaticFanStopAsync(context, clock, 35);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Disabled, context.Runtime.GetStatus().FanStopStatus.AutomationState);

        await SaveAutomaticFanStopSettingsAsync(context, FanStopSettings.Default with { AutomaticEnabled = true });
        await SampleAutomaticFanStopAsync(context, clock, 29);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Stopped, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        Assert.Equal(FanStopSettings.Default with { AutomaticEnabled = true }, owner.CapturedSettings);
    }

    [Theory]
    [InlineData(50, 40)]
    [InlineData(40, 50)]
    public async Task AutomaticFanStopEitherWarmSensorResetsTheSharedEntryInterval(int cpu, int gpu)
    {
        var (context, owner, clock, temperatures) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        temperatures.Readings = new(cpu, gpu, true);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(0, owner.StartCalls);
        temperatures.Readings = new(40, 40, true);
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    public async Task AutomaticFanStopOnlyContinuousMonotonicSamplesCount(int gapSeconds, bool starts)
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(gapSeconds));
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(starts ? 1 : 0, owner.StartCalls);
        if (!starts)
        {
            await SampleAutomaticFanStopAsync(context, clock, 2);
            Assert.Equal(0, owner.StartCalls);
            await SampleAutomaticFanStopAsync(context, clock, 1);
            Assert.Equal(1, owner.StartCalls);
        }
    }

    [Theory]
    [InlineData(FanStopEndMode.Custom, 40, false, false, true)]
    [InlineData(FanStopEndMode.Auto, 40, false, false, false)]
    [InlineData(FanStopEndMode.Auto, 40, true, false, true)]
    [InlineData(FanStopEndMode.Custom, null, true, false, false)]
    [InlineData(FanStopEndMode.Custom, null, true, true, true)]
    public async Task AutomaticFanStopGpuEntryNeedsRealTemperatureOrExplicitPowerDown(
        FanStopEndMode mode, int? gpu, bool inactive, bool poweredDown, bool starts)
    {
        var (context, owner, clock, temperatures) = await CreateAutomaticFanStopContextAsync(
            AutomaticCustomSettings() with { EndMode = mode });
        temperatures.Readings = new(40, gpu, inactive, poweredDown);
        await SampleAutomaticFanStopAsync(context, clock, 4);
        Assert.Equal(starts ? 1 : 0, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopManualAutoPausesUntilResumeAndRequiresANewFullInterval()
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default);
        await SampleAutomaticFanStopAsync(context, clock, 5);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(FanStopAutomationPauseReason.ManualAuto, context.Runtime.GetStatus().FanStopStatus.AutomationPauseReason);
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.ResumeAutomaticFanStopAsync(() => true, default)).Status);
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
    }

    [Theory]
    [InlineData(FanStopEndReason.ThermalThreshold, true)]
    [InlineData(FanStopEndReason.CallerRequest, false)]
    [InlineData(FanStopEndReason.SafetyCondition, false)]
    [InlineData(FanStopEndReason.PowerChanged, false)]
    public async Task AutomaticFanStopOnlyThermalCompletionRearmsAfterNativeAndOwnerRelease(
        FanStopEndReason reason, bool rearms)
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 3);
        Assert.Equal(1, owner.StartCalls);
        owner.Complete(reason, nativeReleased: false, ownerExited: false);
        await SampleAutomaticFanStopAsync(context, clock, 4);
        Assert.Equal(1, owner.StartCalls);
        owner.Complete(reason, nativeReleased: true, ownerExited: false);
        await SampleAutomaticFanStopAsync(context, clock, 4);
        Assert.Equal(1, owner.StartCalls);
        owner.Complete(reason, nativeReleased: true, ownerExited: true);
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(1, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(rearms ? 2 : 1, owner.StartCalls);
        Assert.Equal(0, owner.AutoCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticFanStopFailedAttemptNeverAutomaticallyRetries(bool uncertain)
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        owner.StartSubmission = uncertain ? FanStopSubmission.Uncertain : FanStopSubmission.NotSubmitted;
        await SampleAutomaticFanStopAsync(context, clock, 3);
        Assert.Equal(1, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 10);
        Assert.Equal(1, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Paused, context.Runtime.GetStatus().FanStopStatus.AutomationState);
    }

    [Fact]
    public async Task AutomaticFanStopIdleArmedLowPowerBoundaryPausesWithoutWaitingForMaintenance()
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        context.LowPowerGate.Enter();
        await context.Runtime.PrepareForLowPowerAsync(context.LowPowerGate.Generation, () => true, default);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Paused, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        Assert.Equal(FanStopAutomationPauseReason.SessionChanged, context.Runtime.GetStatus().FanStopStatus.AutomationPauseReason);
        Assert.Equal(0, owner.AutoCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AutomaticFanStopTransientReadFailureRestartsWaitingWithAFullFreshInterval(int failure)
    {
        var (context, owner, clock, temperatures) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        temperatures.Read = _ => failure switch
        {
            0 => ValueTask.FromException<FanTemperatureReadings>(new IOException("Temporary sensor failure")),
            1 => ValueTask.FromException<FanTemperatureReadings>(new OperationCanceledException()),
            2 => ValueTask.FromResult(new FanTemperatureReadings(null, 40, true)),
            _ => SlowReading(),
        };
        ValueTask<FanTemperatureReadings> SlowReading()
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return ValueTask.FromResult(temperatures.Readings);
        }
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Waiting, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        temperatures.Read = null;
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopTemporaryUnverifiedAdmissionResetsWaitingInsteadOfPausing()
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        context.Provider.Available = false;
        // Bounded retries intentionally preserve presentation on an early miss.
        // Exercise actual denied admission without pinning that retry count.
        for (int miss = 0; miss < 5 && context.Runtime.GetStatus().DeviceVerified; miss++)
        { await context.Runtime.ObserveAsync(default); }
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Waiting, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        context.Provider.Available = true;
        await context.Runtime.ObserveAsync(default);
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopCapabilityLossResetsWaitingBeforeReAdmission()
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = false };
        await context.Runtime.ObserveAsync(default);
        Assert.False(context.Runtime.GetStatus().Capabilities!.SupportsFanStop);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Waiting, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticFanStopFreshGuardianAdmissionFailureResetsWaitingBeforeAnyAttempt(bool throws)
    {
        var (context, owner, clock, temperatures) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        int reads = temperatures.ReadCalls;
        if (throws) { owner.AdmissionFailure = new IOException("Temporary admission read failure"); }
        else { owner.Admission = owner.Admission with { PowerKnown = false }; }
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(reads, temperatures.ReadCalls);
        Assert.Equal(FanStopAutomationState.Waiting, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        owner.AdmissionFailure = null;
        owner.Admission = GuardianFanStopBackendTests.Client.SafeAdmission;
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task AutomaticFanStopReadLatencyDoesNotMaskAnUncoveredObservationGap(int uncoveredSeconds, bool starts)
    {
        var (context, owner, clock, temperatures) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await context.Runtime.MaintainFanCurveAsync(default);
        clock.Advance(TimeSpan.FromSeconds(uncoveredSeconds));
        temperatures.Read = _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return ValueTask.FromResult(temperatures.Readings);
        };
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(starts ? 1 : 0, owner.StartCalls);
        if (!starts)
        {
            temperatures.Read = null;
            await SampleAutomaticFanStopAsync(context, clock, 2);
            Assert.Equal(0, owner.StartCalls);
            await SampleAutomaticFanStopAsync(context, clock, 1);
            Assert.Equal(1, owner.StartCalls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticFanStopEnableOrResumeCannotOverwriteConcurrentManualAuto(bool resume)
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(
            resume ? AutomaticCustomSettings() : null);
        if (resume) { await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default); }
        Task<ControlOperationResult>? manualAuto = null;
        bool StillControlsSession()
        {
            manualAuto ??= context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default);
            return true;
        }
        ControlOperationResult result = resume
            ? await context.Runtime.ResumeAutomaticFanStopAsync(StillControlsSession, default)
            : await context.Runtime.SetFanStopSettingsAsync(AutomaticCustomSettings(), StillControlsSession, default);
        Assert.NotNull(manualAuto);
        await manualAuto;
        Assert.Equal(resume ? ControlOperationStatus.Blocked : ControlOperationStatus.Stored, result.Status);
        Assert.Equal(FanStopAutomationState.Paused, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        Assert.Equal(FanStopAutomationPauseReason.ManualAuto, context.Runtime.GetStatus().FanStopStatus.AutomationPauseReason);
        await SampleAutomaticFanStopAsync(context, clock, 4);
        Assert.Equal(0, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopResumeCannotOverwriteLifecycleIngressWhileAlreadyPaused()
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default);
        Task<ControlOperationResult>? lowPower = null;
        bool StillControlsSession()
        {
            if (lowPower is null)
            {
                context.LowPowerGate.Enter();
                lowPower = context.Runtime.PrepareForLowPowerAsync(context.LowPowerGate.Generation, () => true, default);
            }
            return true;
        }
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.ResumeAutomaticFanStopAsync(StillControlsSession, default)).Status);
        Assert.NotNull(lowPower);
        await lowPower;
        Assert.Equal(FanStopAutomationState.Paused, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        await SampleAutomaticFanStopAsync(context, clock, 4);
        Assert.Equal(0, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopDisabledBoundaryCannotSilentlyArmAndEnableRequiresFreshInterval()
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        await SaveAutomaticFanStopSettingsAsync(context, AutomaticCustomSettings() with { AutomaticEnabled = false });
        await context.Runtime.SetPerformanceOverrideAsync(new(PerformancePreset.Silent, OverrideLifetime.Persistent), default);
        Assert.Equal(FanStopAutomationState.Disabled, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        Assert.Equal(FanStopAutomationPauseReason.None, context.Runtime.GetStatus().FanStopStatus.AutomationPauseReason);
        await context.Runtime.SetPerformanceOverrideAsync(new(PerformancePreset.Balanced, OverrideLifetime.Persistent), default);
        await SaveAutomaticFanStopSettingsAsync(context, AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        Assert.Equal(0, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 1);
        Assert.Equal(1, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopOwnedPolicyChangeRequestsAutoWithoutMutatingCapturedPolicy()
    {
        FanStopSettings original = AutomaticCustomSettings();
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(original);
        await SampleAutomaticFanStopAsync(context, clock, 3);
        FanStopSettings changed = original with { CpuLimitCelsius = 65 };
        await SaveAutomaticFanStopSettingsAsync(context, changed);
        Assert.Equal(1, owner.AutoCalls);
        Assert.Equal(1, owner.StartCalls);
        Assert.Equal(original, owner.CapturedSettings);
        Assert.Equal(changed, context.Runtime.GetStatus().FanStopStatus.Settings);
        Assert.Equal(changed, (await context.Store.LoadAsync(default)).Preferences.FanStopSettings);
        await SampleAutomaticFanStopAsync(context, clock, 4);
        Assert.Equal(1, owner.StartCalls);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("shutdown")]
    [InlineData("power-increase")]
    public async Task AutomaticFanStopModeLifecycleAndPowerIncreasePauseEntry(string boundary)
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 2);
        switch (boundary)
        {
            case "mode":
                await context.Runtime.SetPerformanceOverrideAsync(new(PerformancePreset.Silent, OverrideLifetime.Persistent), default);
                break;
            case "shutdown":
                await context.Runtime.PrepareForShutdownAsync(default);
                break;
            case "power-increase":
                context.Telemetry.Current = context.Telemetry.Current with { PowerSource = PowerSource.PluggedIn };
                await context.Runtime.HandlePowerSourceChangedAsync(default);
                break;
        }
        await SampleAutomaticFanStopAsync(context, clock, 4);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(FanStopAutomationState.Paused, context.Runtime.GetStatus().FanStopStatus.AutomationState);
    }

    private static FanStopSettings AutomaticCustomSettings() => FanStopSettings.Default with
    {
        AutomaticEnabled = true,
        EndMode = FanStopEndMode.Custom,
        SecondsBelowLimits = 3,
    };

    private async Task<(TestContext Context, AutomaticFanStopGuardianClient Owner,
        ManualTimeProvider Clock, AutomaticFanStopTemperatureSource Temperatures)>
        CreateAutomaticFanStopContextAsync(FanStopSettings? settings = null)
    {
        var owner = new AutomaticFanStopGuardianClient();
        var clock = new ManualTimeProvider();
        var temperatures = new AutomaticFanStopTemperatureSource();
        TestContext context = CreateContext(true, initialPowerSource: PowerSource.UsbC,
            fanStopBackend: new GuardianFanStopBackend(owner), timeProvider: clock,
            fanTemperatureSource: temperatures, transitionRetryDelay: static (_, _) => Task.CompletedTask);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Balanced };
        await context.Runtime.ObserveAsync(default);
        if (settings is not null) { await SaveAutomaticFanStopSettingsAsync(context, settings); }
        return (context, owner, clock, temperatures);
    }

    private static async Task SaveAutomaticFanStopSettingsAsync(TestContext context, FanStopSettings settings) =>
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.SetFanStopSettingsAsync(settings, () => true, default)).Status);

    // Every call begins with an immediate sample, then advances only the monotonic
    // fake clock. No real delays or private-method invocation establish continuity.
    private static async Task SampleAutomaticFanStopAsync(TestContext context, ManualTimeProvider clock, int seconds)
    {
        await context.Runtime.MaintainFanCurveAsync(default);
        for (int second = 0; second < seconds; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await context.Runtime.MaintainFanCurveAsync(default);
        }
    }

    private sealed class AutomaticFanStopTemperatureSource : IFanTemperatureSource
    {
        public FanTemperatureReadings Readings = new(40, 40, true);
        public int ReadCalls;
        public Func<CancellationToken, ValueTask<FanTemperatureReadings>>? Read;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ReadCalls++;
            return Read?.Invoke(token) ?? ValueTask.FromResult(Readings);
        }
    }

    private sealed class AutomaticFanStopGuardianClient : IFanStopGuardianClient
    {
        public int StartCalls;
        public int AutoCalls;
        public FanStopSettings? CapturedSettings;
        public FanStopAdmission Admission = GuardianFanStopBackendTests.Client.SafeAdmission;
        public Exception? AdmissionFailure;
        public FanStopSubmission StartSubmission = FanStopSubmission.Submitted;
        public FanStopUnavailableReason UnavailableReason => FanStopUnavailableReason.None;
        public FanStopStartupDisposition StartupDisposition => FanStopStartupDisposition.Clear;
        public FanStopGuardianSnapshot Snapshot { get; private set; } = new(Guid.Empty, 0, FanStopGuardianPhase.Idle);
        public event Action? SnapshotChanged;
        public void Notify() => SnapshotChanged?.Invoke();
        public Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return AdmissionFailure is { } failure ? Task.FromException<FanStopAdmission>(failure) : Task.FromResult(Admission);
        }
        public Task<FanStopSubmission> StartAsync(Guid operationId, CancellationToken token) =>
            StartAsync(operationId, FanStopSettings.Default, token);
        public Task<FanStopSubmission> StartAsync(Guid operationId, FanStopSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StartCalls++;
            CapturedSettings = settings;
            if (StartSubmission != FanStopSubmission.NotSubmitted)
            {
                Snapshot = StartSubmission == FanStopSubmission.Uncertain
                    ? new(operationId, 1, FanStopGuardianPhase.RecoveryRequired, Unresolved: true)
                    : new(operationId, 1, FanStopGuardianPhase.Stopped, FanStopObservedState.Stopped, StopConfirmed: true);
            }
            return Task.FromResult(StartSubmission);
        }
        public Task<FanStopSubmission> RequestAutoAsync(Guid operationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(Snapshot.OperationId, operationId);
            AutoCalls++;
            Complete(FanStopEndReason.CallerRequest);
            return Task.FromResult(FanStopSubmission.Submitted);
        }
        public void Complete(FanStopEndReason reason, bool nativeReleased = true, bool ownerExited = true) =>
            Snapshot = Snapshot with
            {
                Revision = Snapshot.Revision + 1,
                Phase = FanStopGuardianPhase.Completed,
                ConfirmedState = FanStopObservedState.Automatic,
                AutoConfirmed = true,
                CleanupConfirmed = true,
                JournalComplete = true,
                NativeReleased = nativeReleased,
                OwnerExited = ownerExited,
                EndReason = reason,
            };
    }
}
