using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoolingPadShutdown_FailureOrCancellationCannotDelayKeyboardAndLaptopFanRelease(bool cancel)
    {
        var pad = new LifecyclePad();
        var (context, _) = await CreateActiveLifecyclePadContextAsync(pad);
        await context.Runtime.SetFanOverrideAsync(new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent), default);
        context.Device.DriverModeActive = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pad.Restore = async token =>
        {
            entered.TrySetResult();
            if (cancel) { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            else
            {
                await finish.Task.WaitAsync(token);
                throw new CoolingPadOperationException("Synthetic pad restoration failure.", true);
            }
        };

        Task<ControlOperationResult> shutdown = context.Runtime.PrepareForShutdownAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Prove native laptop cleanup has already happened while pad restoration is still blocked.
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, context.Device.DriverModeReleaseCalls);
            Assert.False(context.Device.DriverModeActive);
            Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
            finish.TrySetResult();
            ControlOperationResult result = await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ControlOperationStatus.Failed, result.Status);
            Assert.True(context.Runtime.GetStatus().CoolingPad.RecoveryRequired);
            Assert.True(context.Runtime.GetStatus().CoolingPad.WritesPaused);
            Assert.Equal(1, pad.RestoreCalls);
        }
        finally
        {
            finish.TrySetResult();
            try { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CoolingPadShutdown_SuccessfulRestoreWithHeldDisposalHonorsCleanupCancellation()
    {
        var pad = new LifecyclePad();
        var (context, _) = await CreateActiveLifecyclePadContextAsync(pad);
        context.Device.DriverModeActive = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pad.Dispose = async () =>
        {
            entered.TrySetResult();
            await retired.Task;
        };

        Task<ControlOperationResult> shutdown = context.Runtime.PrepareForShutdownAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, context.Device.DriverModeReleaseCalls);
            Assert.False(context.Device.DriverModeActive);
            Assert.False(shutdown.IsCompleted);
            ControlOperationResult result = await shutdown.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(ControlOperationStatus.Failed, result.Status);
            Assert.Equal(1, pad.RestoreCalls);
            Assert.True(context.Runtime.GetStatus().CoolingPad.WritesPaused);
            Assert.False(retired.Task.IsCompleted);
        }
        finally
        {
            retired.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CoolingPadShutdown_PadOnlyFailureOrBlockLeavesLaptopCleanupConfirmed()
    {
        var pad = new LifecyclePad
        {
            Restore = _ => Task.FromException(
                new CoolingPadOperationException("Synthetic pad restoration failure.", true)),
        };
        var (context, _) = await CreateActiveLifecyclePadContextAsync(pad);
        await context.Runtime.SetFanOverrideAsync(new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent), default);
        context.Device.DriverModeActive = true;

        ServiceStopCleanupResult failed = await context.Runtime.PrepareForServiceStopAsync(default);

        // The laptop was handed back; only the pad was not. The pad keeps its journal.
        Assert.Equal(ControlOperationStatus.Failed, failed.Result.Status);
        Assert.Contains("Cooling-pad fan cleanup:", failed.Result.Message, StringComparison.Ordinal);
        Assert.Equal(ControlOperationStatus.Applied, failed.LaptopStatus);
        Assert.False(context.Device.DriverModeActive);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.True(context.Runtime.GetStatus().CoolingPad.RecoveryRequired);
        Assert.True(context.Runtime.GetStatus().CoolingPad.WritesPaused);

        // A retained journal keeps writes paused until the user checks the pad, so a later
        // stop is Blocked without a pad write, as on every RZ09-0581 stop on 2026-11-24.
        ServiceStopCleanupResult blocked = await context.Runtime.PrepareForServiceStopAsync(default);

        Assert.Equal(ControlOperationStatus.Blocked, blocked.Result.Status);
        Assert.Contains("Cooling-pad fan cleanup: Read the cooling pad before restoring retained settings.",
            blocked.Result.Message, StringComparison.Ordinal);
        Assert.Contains(blocked.LaptopStatus, new[] { ControlOperationStatus.Applied, ControlOperationStatus.Stored });
        Assert.Equal(1, pad.RestoreCalls);
    }

    [Fact]
    public async Task CoolingPadShutdown_LaptopCleanupFailureStillDecidesLaptopStatusAfterPadRestore()
    {
        var pad = new LifecyclePad();
        var (context, _) = await CreateActiveLifecyclePadContextAsync(pad);
        context.Device.DriverModeActive = true;
        context.Device.DriverModeReleaseFailure = new IOException("Synthetic keyboard release failure.");

        ServiceStopCleanupResult result = await context.Runtime.PrepareForServiceStopAsync(default);

        Assert.Equal(1, pad.RestoreCalls);
        Assert.False(context.Runtime.GetStatus().CoolingPad.RecoveryRequired);
        Assert.DoesNotContain(result.LaptopStatus, new[] { ControlOperationStatus.Applied, ControlOperationStatus.Stored });
        Assert.Equal(result.LaptopStatus, result.Result.Status);
    }

    [Fact]
    public async Task CoolingPadLowPower_ResumeDuringKeyboardReleaseBlocksPadRestoreAtSubmission()
    {
        var gate = new LowPowerGate();
        var pad = new LifecyclePad();
        var (context, _) = await CreateActiveLifecyclePadContextAsync(pad, gate);
        context.Device.DriverModeActive = true;
        context.Device.DriverModeReleaseOverride = _ =>
        {
            context.Device.DriverModeActive = false;
            gate.Exit();
            return Task.FromResult<bool?>(false);
        };
        gate.Enter();
        long suspendedGeneration = gate.Generation;

        _ = await context.Runtime.PrepareForLowPowerAsync(suspendedGeneration, () => true, default);

        Assert.Equal(1, context.Device.DriverModeReleaseCalls);
        Assert.False(context.Device.DriverModeActive);
        Assert.False(gate.IsActiveForGeneration(suspendedGeneration));
        Assert.Equal(0, pad.RestoreCalls);
        Assert.Equal(1, pad.FanWrites);
        Assert.True(context.Runtime.GetStatus().CoolingPad.RecoveryRequired);
    }

    [Fact]
    public async Task CoolingPadReview_OptionalStoreFailureDoesNotAbortLaptopUserActivation()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(
            new Dictionary<string, ProfileSet> { [user.Value] = ProfileDefaults.CreateSafeDefaults() }, directory);
        TestContext context = CreateWritableContext(userConfigurationRepository: repository,
            coolingPadDeviceProvider: new LifecyclePadProvider(new LifecyclePad()),
            coolingPadStateDirectory: Path.Combine(directory, "unprotected-accessory-state"));
        await context.Runtime.ObserveAsync(default);

        await context.Runtime.ActivateUserAsync(user, () => true, default);

        ControlOperationResult laptop = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent), default);
        Assert.Equal(ControlOperationStatus.Applied, laptop.Status);
        Assert.True(context.Runtime.GetStatus().CoolingPad.WritesPaused);
        Assert.False(context.Runtime.GetStatus().CoolingPad.Active);
    }

    [Fact]
    public async Task CoolingPadReview_UserHandoffRestoresPriorBaselineWithoutTransferringIntent()
    {
        var first = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var second = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        var repository = new RecordingUserConfigurationRepository(new Dictionary<string, ProfileSet>
        {
            [first.Value] = ProfileDefaults.CreateSafeDefaults(),
            [second.Value] = ProfileDefaults.CreateSafeDefaults(),
        }, directory);
        var pad = new LifecyclePad();
        TestContext context = CreateWritableContext(userConfigurationRepository: repository,
            coolingPadDeviceProvider: new LifecyclePadProvider(pad));
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.ActivateUserAsync(first, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetCoolingPadSettingsAsync(
            new CoolingPadSettings { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 1500 }, first, () => true, default)).Status);

        await context.Runtime.ActivateUserAsync(second, () => true, default);

        Assert.Equal(1, pad.RestoreCalls);
        Assert.False(context.Runtime.GetStatus().CoolingPad.Active);
        Assert.Equal(CoolingPadFanMode.Keep, context.Runtime.GetStatus().CoolingPad.Requested.FanMode);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetCoolingPadSettingsAsync(
            new CoolingPadSettings { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 2000 }, second, () => true, default)).Status);
        Assert.Equal(2, pad.FanWrites);
    }

    [Fact]
    public async Task CoolingPadResumesSavedSettingsAfterWakeButNotAfterShutdownHandback()
    {
        var time = new ManualTimeProvider();
        var gate = new LowPowerGate();
        var pad = new LifecyclePad();
        var (context, _) = await CreateActiveLifecyclePadContextAsync(pad, gate, timeProvider: time);
        gate.Enter();
        _ = await context.Runtime.PrepareForLowPowerAsync(gate.Generation, () => true, default);
        Assert.Equal(1, pad.RestoreCalls);

        // Asleep: the handback stands.
        await context.Runtime.MaintainCoolingPadAsync(default);
        Assert.Equal(1, pad.FanWrites);
        Assert.False(context.Runtime.GetStatus().CoolingPad.Active);

        // Awake: the saved settings are applied again, as an Apply would.
        gate.Exit();
        time.Advance(CoolingPadControlRuntime.ResumeAdmissionInterval);
        await context.Runtime.MaintainCoolingPadAsync(default);
        Assert.True(context.Runtime.GetStatus().CoolingPad.Active);
        Assert.Equal(2, pad.FanWrites);

        // Shutdown hands it back for good; the next service resumes it instead.
        _ = await context.Runtime.PrepareForShutdownAsync(default);
        Assert.Equal(2, pad.RestoreCalls);
        time.Advance(CoolingPadControlRuntime.ResumeRetryInterval);
        await context.Runtime.MaintainCoolingPadAsync(default);
        Assert.False(context.Runtime.GetStatus().CoolingPad.Active);
        Assert.Equal(2, pad.FanWrites);
    }

    [Theory]
    // Notify me, and an upgraded Restore them whose disclosure the user has not seen yet.
    [InlineData(ThermalControlPolicy.Strict, ThermalPolicyUpgradeDisclosureState.NotRequired)]
    [InlineData(ThermalControlPolicy.MaintainDesiredState, ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending)]
    public async Task CoolingPadResumptionFollowsTheNotifyMePolicy(ThermalControlPolicy policy,
        ThermalPolicyUpgradeDisclosureState disclosure)
    {
        var time = new ManualTimeProvider();
        var gate = new LowPowerGate();
        var pad = new LifecyclePad();
        string machineRoot = Path.Combine(directory, $"pad-{policy}-{disclosure}");
        var user = new SecurityIdentifier("S-1-5-21-100-200-300-1001");
        string userRoot = Path.Combine(machineRoot, "users", user.Value);
        var validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();
        var configurationStore = new BatteryConfigurationStore(Path.Combine(userRoot, "profiles.json"),
            Path.Combine(userRoot, "battery-preferences.json"), Path.Combine(userRoot, "battery-configuration.commit.json"), validator);
        BatteryConfiguration configuration = await configurationStore.LoadAsync(default);
        await configurationStore.SaveConfigurationAsync(configuration.Profiles,
            configuration.Preferences with { ThermalControlPolicy = policy, ThermalPolicyUpgradeDisclosure = disclosure }, default);
        TestContext context = CreateContext(providerAvailable: true, lowPowerGate: gate, timeProvider: time,
            userConfigurationRepository: new ThermalTestUserConfigurationRepository(machineRoot, validator),
            coolingPadDeviceProvider: new LifecyclePadProvider(pad));
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.ActivateUserAsync(user, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetCoolingPadSettingsAsync(
            new CoolingPadSettings { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 1500 }, user, () => true, default)).Status);
        gate.Enter();
        _ = await context.Runtime.PrepareForLowPowerAsync(gate.Generation, () => true, default);
        Assert.Equal(1, pad.RestoreCalls);

        // "Notify me" leaves the pad on its own settings after the handback; "Restore them"
        // resumes them, as CoolingPadResumesSavedSettingsAfterWakeButNotAfterShutdownHandback shows.
        gate.Exit();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            time.Advance(CoolingPadControlRuntime.ResumeRetryInterval);
            await context.Runtime.MaintainCoolingPadAsync(default);
        }
        Assert.False(context.Runtime.GetStatus().CoolingPad.Active);
        Assert.Equal(1, pad.FanWrites);
    }

    private async Task<(TestContext Context, SecurityIdentifier User)> CreateActiveLifecyclePadContextAsync(
        LifecyclePad pad, LowPowerGate? gate = null, bool laptopAvailable = true, TimeProvider? timeProvider = null)
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(
            new Dictionary<string, ProfileSet> { [user.Value] = ProfileDefaults.CreateSafeDefaults() }, directory);
        TestContext context = CreateWritableContext(
            userConfigurationRepository: repository, lowPowerGate: gate, timeProvider: timeProvider,
            coolingPadDeviceProvider: new LifecyclePadProvider(pad));
        context.Provider.Available = laptopAvailable;
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.ActivateUserAsync(user, () => true, default);
        ControlOperationResult applied = await context.Runtime.SetCoolingPadSettingsAsync(
            new CoolingPadSettings { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 1500 }, user, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.True(context.Runtime.GetStatus().CoolingPad.Active);
        return (context, user);
    }

    private sealed class LifecyclePadProvider(LifecyclePad pad) : ICoolingPadDeviceProvider
    {
        public CoolingPadConnectionState ObserveConnection() => CoolingPadConnectionState.PresentExact;
        public Task<ICoolingPadDevice?> OpenAsync(CancellationToken token) => Task.FromResult<ICoolingPadDevice?>(pad);
    }

    private sealed class LifecyclePad : ICoolingPadDevice
    {
        public string Identity => "lifecycle-pad";
        private CoolingPadDeviceSnapshot snapshot = new("lifecycle-pad", null, 1000)
        { FanConfiguration = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Quiet, 1000) };
        internal Func<CancellationToken, Task>? Restore;
        internal Func<Task>? Dispose;
        internal int RestoreCalls;
        internal int FanWrites;
        // Runs inside a fan write before its admission check, as a concurrent service task would.
        internal Func<Task>? DuringFanWrite;
        public Task<CoolingPadDeviceSnapshot> ReadSnapshotAsync(CancellationToken token) => Task.FromResult(snapshot);
        public async Task SetFanAsync(CoolingPadFanMode mode, int? rpm, Func<bool> allowed, CancellationToken token,
            CoolingPadFanPreset? smartPreset = null)
        {
            if (DuringFanWrite is { } during) { await during(); }
            if (!allowed()) { throw new CoolingPadOperationException("Admission closed.", false); }
            FanWrites++;
            snapshot = snapshot with { FanMode = mode, TargetRpm = rpm };
        }
        public Task SetLightingAsync(CoolingPadSettings settings, Func<bool> allowed, CancellationToken token) =>
            throw new InvalidOperationException("This lifecycle test must not write pad lighting.");
        public Task PushMirrorFrameAsync(IReadOnlyList<CoolingPadColor> colors, Func<bool> allowed, CancellationToken token) =>
            throw new InvalidOperationException("This lifecycle test must not render mirror frames.");
        public async Task RestoreAsync(CoolingPadDeviceSnapshot baseline, bool fan, bool lighting,
            Func<bool> allowed, CancellationToken token)
        {
            if (!allowed()) { throw new CoolingPadOperationException("Admission closed.", false); }
            RestoreCalls++;
            if (Restore is { } restore) { await restore(token); }
            snapshot = baseline;
        }
        public ValueTask DisposeAsync() => Dispose is { } dispose ? new(dispose()) : ValueTask.CompletedTask;
    }
}
