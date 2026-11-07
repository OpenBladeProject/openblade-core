using OpenBlade.Contracts;
using OpenBlade.Tray;
namespace OpenBlade.Tray.Tests;

public sealed class CpuTuningViewModelTests
{
    private static DeviceCapabilities Caps(DeviceFeatureAccess access = DeviceFeatureAccess.ReadWrite) => new(
        new HashSet<PerformancePreset>(), new HashSet<FanMode>(), new HashSet<LightingEffect>(), new HashSet<int>(), false, false)
    { CpuVoltageOptimizer = new(new(-30, -1, 1), access) { SupportsEnableDisable = true } };
    private static CpuTuningStatus Status(int? live = -10) => new(
        new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, -20, [])),
        live.HasValue ? new(null, [new(0, live.Value), new(1, live.Value)]) : null,
        new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available),
        new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available), CpuTuningPendingBoot.Unknown, false);

    [Fact]
    public void MissingLiveReadbackNeverUsesSavedValues()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(null) with { PendingBoot = CpuTuningPendingBoot.Requested }, Caps());
        Assert.Equal(-1, vm.CurveValue); Assert.False(vm.CurveEnabled);
        Assert.Contains("unknown", vm.LiveSummary); Assert.True(vm.ShowAvailabilitySummary);
        Assert.False(vm.CanApply); Assert.False(vm.CanRestore);
        vm.UpdateStatus(Status(-7), Caps());
        Assert.Equal(-7, vm.CurveValue); Assert.True(vm.CurveEnabled); Assert.False(vm.ShowAvailabilitySummary); Assert.Empty(vm.AvailabilitySummary);
    }
    [Fact]
    public void ConfirmedLiveValueInitializesOnceAndRefreshPreservesEdits()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(-7), Caps());
        Assert.True(vm.CurveEnabled); Assert.Equal(-7, vm.CurveValue);
        vm.CurveValue = -23; vm.CurveEnabled = false;
        vm.UpdateStatus(Status(-9), Caps());
        Assert.Equal(-23, vm.CurveValue); Assert.False(vm.CurveEnabled);
        Assert.Contains("-9", vm.LiveSummary);
    }
    [Fact]
    public void PreObservationEditsRemainIntentOnly()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true) { CurveValue = -21 };
        vm.UpdateStatus(Status(-6), Caps());
        Assert.Equal(-21, vm.CurveValue); Assert.False(vm.CurveEnabled);
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
    [Fact]
    public async Task EveryApplyAndRestoreIsLiveCurveOnly()
    {
        var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true);
        vm.UpdateStatus(Status(-5), Caps()); await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(CpuTuningTarget.LiveNow, client.Target);
        Assert.Null(client.Settings!.PowerLimitWatts); Assert.Equal(-5, client.Settings.CurveOptimizer!.Value);
        vm.CurveEnabled = false; await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, client.Settings.CurveOptimizer!.EffectiveValue);
        Assert.Equal(0, client.ObserveRequests);
        await vm.RefreshCommand.ExecuteAsync(null); Assert.True(client.InstalledOptIn); Assert.Equal(1, client.ObserveRequests);
        vm.UpdateStatus(Status() with { RestorePending = true, LiveRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer) }, Caps());
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(1, client.RestoreRequests); Assert.Equal(CpuTuningTarget.LiveNow, client.Target);
    }
    [Theory]
    [InlineData(CpuTuningFeatures.None)]
    [InlineData(CpuTuningFeatures.PowerLimit)]
    [InlineData(CpuTuningFeatures.PowerLimit | CpuTuningFeatures.CurveOptimizer)]
    [InlineData(CpuTuningFeatures.CurveOptimizer)]
    public async Task RestoreAdmitsOnlyExactLiveCurveRecovery(CpuTuningFeatures features)
    {
        var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true);
        var recovery = new CpuTuningRecoveryStatus(CpuTuningRecoveryState.RestoreReady, features);
        vm.UpdateStatus(Status() with { RestorePending = true, LiveRecovery = recovery }, Caps());
        Assert.Equal(features == CpuTuningFeatures.CurveOptimizer, vm.CanRestore);
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(features == CpuTuningFeatures.CurveOptimizer ? 1 : 0, client.RestoreRequests);
    }
    [Theory]
    [InlineData(DeviceFeatureAccess.ObservedOnly)]
    [InlineData(DeviceFeatureAccess.ReadOnly)]
    [InlineData((DeviceFeatureAccess)99)]
    public void NonWritableFeatureCannotWrite(DeviceFeatureAccess access)
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(), Caps(access)); Assert.False(vm.CanEditCurve); Assert.False(vm.CanApply);
    }
    [Fact]
    public void SafetyLiveBackendAndBlockedRecoveryRemainRequired()
    {
        bool safe = true; var vm = new CpuTuningViewModel(new Client(), () => safe, () => true);
        vm.UpdateStatus(Status() with { Live = new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable) }, Caps());
        Assert.False(vm.CanApply);
        vm.UpdateStatus(Status(), Caps()); safe = false; Assert.False(vm.CanApply); Assert.False(vm.CanRestore); Assert.True(vm.CanRefresh);
        safe = true;
        vm.UpdateStatus(Status() with { LiveRecovery = new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer) }, Caps());
        Assert.False(vm.CanApply); Assert.True(vm.ShowRecoverySummary); Assert.Contains("blocked", vm.RecoverySummary);
    }
    [Theory]
    [InlineData(CpuTuningFeatures.PowerLimit, false)]
    [InlineData(CpuTuningFeatures.PowerLimit | CpuTuningFeatures.CurveOptimizer, false)]
    [InlineData(CpuTuningFeatures.CurveOptimizer, true)]
    public async Task PendingPowerRecoveryBlocksApplyButCurveOwnershipRemainsEditable(CpuTuningFeatures features, bool allowed)
    {
        var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true);
        vm.UpdateStatus(Status() with { RestorePending = true, LiveRecovery = new(CpuTuningRecoveryState.RestoreReady, features) }, Caps());
        Assert.Equal(allowed, vm.CanApply);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(allowed, client.Settings is not null);
    }
    [Fact]
    public void SavedRecoveryNeverExposesLiveRestore()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status() with { RestorePending = true, FirmwareRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer) }, Caps());
        Assert.False(vm.CanRestore);
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrepresentableLiveValueNeedsDeliberateChoiceButOwnedRestoreRemainsAvailable(bool mixed)
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status() with { LiveState = new(null, mixed ? [new(0, -10), new(1, -11)] : [new(0, -40), new(1, -40)]), RestorePending = true, LiveRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer) }, Caps());
        Assert.Equal(-1, vm.CurveValue); Assert.True(vm.CanEditCurve); Assert.False(vm.CanApply); Assert.True(vm.CanRestore);
        if (mixed) { Assert.Contains("mixed", vm.LiveSummary); }
        Assert.DoesNotContain("PPT", vm.LiveSummary);
        vm.CurveEnabled = true; Assert.True(vm.CanApply);
        vm.CurveEnabled = false; Assert.True(vm.CanApply); Assert.Equal(0, vm.Settings.CurveOptimizer!.EffectiveValue);
    }
    [Fact]
    public void DeferredApplyKeepsClickedValueAndRechecksLiveAdmission()
    {
        PumpingSynchronizationContext.Run(async _ =>
        {
            var client = new Client(); var vm = new CpuTuningViewModel(client, () => true, () => true);
            vm.UpdateStatus(Status(-10), Caps()); Task request = vm.ApplyCommand.ExecuteAsync(null); vm.CurveValue = -24;
            await request; Assert.Equal(CpuTuningTarget.LiveNow, client.Target); Assert.Equal(-10, client.Settings!.CurveOptimizer!.Value);
            request = vm.ApplyCommand.ExecuteAsync(null); vm.UpdateStatus(Status(null), Caps()); client.ClearSettings();
            await request; Assert.Null(client.Settings);
        });
    }
    private sealed class Client : OpenBlade.Testing.UnavailableServiceClient, IServiceClient
    {
        public CpuTuningTarget Target { get; private set; }
        public CpuTuningSettings? Settings { get; private set; }
        public bool InstalledOptIn { get; private set; }
        public int RestoreRequests { get; private set; }
        public int ObserveRequests { get; private set; }
        public void ClearSettings() => Settings = null;
        public Task<ControlOperationResult> SetCpuTuningAsync(CpuTuningTarget target, CpuTuningSettings settings, CancellationToken token)
        { Target = target; Settings = settings; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked, "test")); }
        public Task<ControlOperationResult> ObserveCpuTuningAsync(bool useInstalledBackend, CancellationToken token)
        { ObserveRequests++; InstalledOptIn = useInstalledBackend; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "test")); }
        public Task<ControlOperationResult> RestoreCpuTuningAsync(CpuTuningTarget target, CancellationToken token)
        { Target = target; RestoreRequests++; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked, "test")); }
    }
}
