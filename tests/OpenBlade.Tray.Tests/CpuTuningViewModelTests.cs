using OpenBlade.Contracts;
using OpenBlade.Tray;
namespace OpenBlade.Tray.Tests;

public sealed class CpuTuningViewModelTests
{
    private static DeviceCapabilities Caps(DeviceFeatureAccess access = DeviceFeatureAccess.ReadWrite) => new(
        new HashSet<PerformancePreset>(), new HashSet<FanMode>(), new HashSet<LightingEffect>(), new HashSet<int>(), false, false)
    { CpuPowerLimit = new(new(45, 95, 1), access), CpuVoltageOptimizer = new(new(-30, -1, 1), access) { SupportsEnableDisable = true } };
    private static CpuTuningStatus Status(int saved = -10, int? live = null) => new(
        new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, saved, [])),
        live.HasValue ? new(null, [new(0, live.Value), new(1, live.Value)]) : null,
        new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available),
        new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available), CpuTuningPendingBoot.Unknown, false);

    [Fact]
    public void ConfirmedTargetsInitializeOnceAndRefreshPreservesEdits()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        Assert.Equal(-1, vm.CurveValue);
        vm.UpdateStatus(Status(-12, -7), Caps());
        Assert.True(vm.CurveEnabled); Assert.Equal(-12, vm.CurveValue);
        vm.CurveValue = -23; vm.CurveEnabled = false;
        vm.UpdateStatus(Status(-18, -9), Caps());
        Assert.Equal(-23, vm.CurveValue); Assert.False(vm.CurveEnabled);
        vm.LiveNow = true;
        Assert.Equal(-9, vm.CurveValue); Assert.True(vm.CurveEnabled);
        vm.LiveNow = false;
        Assert.Equal(-23, vm.CurveValue); Assert.False(vm.CurveEnabled);
    }
    [Fact]
    public void PreObservationEditsAndMissingLiveStateNeverUseSavedValues()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true) { CurveValue = -21 };
        vm.UpdateStatus(Status(-20), Caps());
        Assert.Equal(-21, vm.CurveValue); Assert.False(vm.CurveEnabled);
        vm.LiveNow = true;
        Assert.Equal(-1, vm.CurveValue); Assert.Contains("unknown", vm.LiveSummary);
        Assert.Contains("-20", vm.SavedSummary);
        vm.UpdateStatus(Status(-20, -6), Caps());
        Assert.Equal(-6, vm.CurveValue); Assert.True(vm.CurveEnabled);
    }
    [Fact]
    public void OffRemembersNegativeOffsetAndToggleDoesNotApply()
    {
        var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true);
        vm.UpdateStatus(Status(0), Caps());
        Assert.False(vm.CurveEnabled); Assert.Equal(-1, vm.CurveValue);
        vm.CurveValue = -24; vm.CurveEnabled = true; vm.CurveEnabled = false;
        Assert.Equal(-24, vm.CurveValue); Assert.Equal(0, vm.Settings.CurveOptimizer!.EffectiveValue);
        Assert.Null(client.Settings);
        vm.CurveEnabled = true; Assert.Equal(-24, vm.Settings.CurveOptimizer.EffectiveValue);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryApplyIsCurveOnlyForSelectedTarget(bool live)
    {
        var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true) { LiveNow = live };
        vm.UpdateStatus(Status(-10, -5), Caps()); await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(live ? CpuTuningTarget.LiveNow : CpuTuningTarget.FirmwareNextBoot, client.Target);
        Assert.Null(client.Settings!.PowerLimitWatts); Assert.NotNull(client.Settings.CurveOptimizer);
        vm.CurveEnabled = false; await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Null(client.Settings.PowerLimitWatts); Assert.Equal(0, client.Settings.CurveOptimizer!.EffectiveValue);
        vm.UseInstalledBackend = true; await vm.RefreshCommand.ExecuteAsync(null); Assert.True(client.InstalledOptIn);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreAdmitsOnlyExactCurveRecovery(bool live)
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true) { LiveNow = live, CurveValue = -31 };
        foreach (var features in new[] { CpuTuningFeatures.None, CpuTuningFeatures.PowerLimit,
            CpuTuningFeatures.PowerLimit | CpuTuningFeatures.CurveOptimizer, CpuTuningFeatures.CurveOptimizer })
        {
            var recovery = new CpuTuningRecoveryStatus(CpuTuningRecoveryState.RestoreReady, features);
            vm.UpdateStatus(Status(live: -5) with { RestorePending = true, FirmwareRecovery = recovery, LiveRecovery = recovery }, Caps() with { CpuPowerLimit = null });
            Assert.Equal(features == CpuTuningFeatures.CurveOptimizer, vm.CanRestore);
            Assert.Equal(features == CpuTuningFeatures.CurveOptimizer, vm.RestoreCommand.CanExecute(null));
        }
    }
    [Fact]
    public void SafetySelectedBackendFeatureAndBlockedRecoveryRemainRequired()
    {
        bool safe = true; var vm = new CpuTuningViewModel(new Client(), () => safe, () => true);
        vm.UpdateStatus(Status(), Caps(DeviceFeatureAccess.ObservedOnly)); Assert.True(vm.CanApply);
        vm.LiveNow = true; Assert.False(vm.CanApply); vm.LiveNow = false;
        vm.UpdateStatus(Status() with { Firmware = new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Available) }, Caps()); Assert.False(vm.CanApply);
        vm.UpdateStatus(Status(), Caps()); safe = false; Assert.False(vm.CanApply); Assert.False(vm.CanRestore); Assert.True(vm.CanRefresh);
        safe = true;
        vm.UpdateStatus(Status() with { LiveRecovery = new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer) }, Caps()); Assert.False(vm.CanApply);
    }
    [Theory]
    [InlineData(DeviceFeatureAccess.ReadOnly)]
    [InlineData((DeviceFeatureAccess)99)]
    public void ReadOnlyOrInvalidFeatureCannotWrite(DeviceFeatureAccess access)
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(), Caps(access)); Assert.False(vm.CanEditCurve); Assert.False(vm.CanApply);
    }
    [Fact]
    public void RangeStepAndDisableSupportStillGateApply()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        var caps = Caps() with { CpuVoltageOptimizer = new(new(-20, -2, 2), DeviceFeatureAccess.ReadWrite) { SupportsEnableDisable = true } };
        vm.UpdateStatus(Status(), caps); Assert.True(vm.CanApply);
        vm.CurveValue = -9; Assert.False(vm.CanApply); vm.CurveEnabled = false; Assert.True(vm.CanApply);
        vm.UpdateStatus(Status(), caps with { CpuVoltageOptimizer = caps.CpuVoltageOptimizer! with { SupportsEnableDisable = false } }); Assert.False(vm.CanApply);
    }
    [Fact]
    public void MixedRunningAndMissingOptimizerArePresentedHonestly()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true) { LiveNow = true };
        vm.UpdateStatus(Status() with { LiveState = new(new(65, 85000, 80000), [new(0, -10), new(1, -11)]) }, Caps());
        Assert.Contains("mixed", vm.LiveSummary); Assert.DoesNotContain("PPT", vm.LiveSummary); Assert.DoesNotContain("PPT", vm.SavedSummary);
        Assert.Equal(-1, vm.CurveValue);
        vm.UpdateStatus(Status(), Caps() with { CpuVoltageOptimizer = null }); Assert.False(vm.IsVisible); Assert.False(vm.CanApply);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidSelectedReadbackBlocksEditingAndDoesNotBorrowOtherTarget(bool live)
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true) { LiveNow = live };
        var status = live ? Status() : Status(live: -5) with { SavedConfiguration = null };
        var ready = new CpuTuningRecoveryStatus(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer);
        status = status with { RestorePending = true, FirmwareRecovery = ready, LiveRecovery = ready };
        vm.UpdateStatus(status, Caps()); Assert.False(vm.CanEditCurve); Assert.False(vm.CanApply); Assert.False(vm.CanRestore);
        Assert.Contains("unavailable", vm.AvailabilitySummary);
        status = live ? Status() with { LiveState = new(new(65, null, null), []) }
            : Status() with { SavedConfiguration = new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, -70000, [])) };
        vm.UpdateStatus(status, Caps()); Assert.False(vm.CanApply);
    }
    [Fact]
    public async Task MixedRecoveryCannotDispatchRestoreRequest()
    {
        var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true);
        vm.UpdateStatus(Status() with
        {
            RestorePending = true,
            FirmwareRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.PowerLimit | CpuTuningFeatures.CurveOptimizer)
        }, Caps());
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(0, client.RestoreRequests);
    }
    [Fact]
    public void DeferredApplyKeepsClickedTargetAndValueAndRechecksAdmission()
    {
        PumpingSynchronizationContext.Run(async _ =>
        {
            var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true);
            vm.UpdateStatus(Status(-10), Caps());
            Task request = vm.ApplyCommand.ExecuteAsync(null);
            vm.LiveNow = true; vm.CurveValue = -24;
            await request;
            Assert.Equal(CpuTuningTarget.FirmwareNextBoot, client.Target);
            Assert.Equal(-10, client.Settings!.CurveOptimizer!.Value);
            Assert.Null(client.Settings.PowerLimitWatts);
            vm.LiveNow = false;
            request = vm.ApplyCommand.ExecuteAsync(null);
            vm.UpdateStatus(Status() with { SavedConfiguration = null }, Caps());
            client.ClearSettings();
            await request;
            Assert.Null(client.Settings);
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrepresentableInitialValueNeedsDeliberateChoiceButOwnedRestoreRemainsAvailable(bool mixed)
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true) { LiveNow = true };
        var live = new CpuLiveState(null, mixed ? [new(0, -10), new(1, -11)] : [new(0, -40), new(1, -40)]);
        vm.UpdateStatus(Status() with
        {
            LiveState = live,
            RestorePending = true,
            LiveRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer)
        }, Caps());
        Assert.True(vm.CanEditCurve); Assert.False(vm.CanApply); Assert.True(vm.CanRestore);
        vm.CurveEnabled = true; Assert.True(vm.CanApply);
        vm.CurveEnabled = false; Assert.True(vm.CanApply);
        Assert.Equal(0, vm.Settings.CurveOptimizer!.EffectiveValue);
    }
    private sealed class Client : OpenBlade.Testing.UnavailableServiceClient, IServiceClient
    {
        public CpuTuningTarget Target { get; private set; }
        public CpuTuningSettings? Settings { get; private set; }
        public bool InstalledOptIn { get; private set; }
        public int RestoreRequests { get; private set; }
        public void ClearSettings() => Settings = null;
        public Task<ControlOperationResult> SetCpuTuningAsync(CpuTuningTarget target, CpuTuningSettings settings, CancellationToken token)
        { Target = target; Settings = settings; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked, "test")); }
        public Task<ControlOperationResult> ObserveCpuTuningAsync(bool useInstalledBackend, CancellationToken token)
        { InstalledOptIn = useInstalledBackend; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "test")); }
        public Task<ControlOperationResult> RestoreCpuTuningAsync(CpuTuningTarget target, CancellationToken token)
        { RestoreRequests++; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked, "test")); }
    }
}
