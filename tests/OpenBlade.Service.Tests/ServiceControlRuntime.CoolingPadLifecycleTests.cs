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

    private async Task<(TestContext Context, SecurityIdentifier User)> CreateActiveLifecyclePadContextAsync(
        LifecyclePad pad, LowPowerGate? gate = null)
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(
            new Dictionary<string, ProfileSet> { [user.Value] = ProfileDefaults.CreateSafeDefaults() }, directory);
        TestContext context = CreateWritableContext(
            userConfigurationRepository: repository, lowPowerGate: gate,
            coolingPadDeviceProvider: new LifecyclePadProvider(pad));
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
        public Task<CoolingPadDeviceSnapshot> ReadSnapshotAsync(CancellationToken token) => Task.FromResult(snapshot);
        public Task SetFanAsync(CoolingPadFanMode mode, int? rpm, Func<bool> allowed, CancellationToken token)
        {
            if (!allowed()) { throw new CoolingPadOperationException("Admission closed.", false); }
            FanWrites++;
            snapshot = snapshot with { FanMode = mode, TargetRpm = rpm };
            return Task.CompletedTask;
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
