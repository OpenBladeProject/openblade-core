using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CpuTuningViewModelTests
{
    private static DeviceCapabilities Capabilities(DeviceFeatureAccess access) => new(new HashSet<PerformancePreset>(), new HashSet<FanMode>(),
        new HashSet<LightingEffect>(), new HashSet<int>(), false, false)
    {
        CpuPowerLimit = new(new(45, 95, 1), access),
        CpuVoltageOptimizer = new(new(-30, -1, 1), access) { SupportsEnableDisable = true },
    };
    private static CpuTuningStatus Status(DeviceFeatureAccess access, bool restore = false) => new(
        new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, -10, [])), null,
        new(access, CpuTuningBackendAvailability.Available),
        new(access, CpuTuningBackendAvailability.Available), CpuTuningPendingBoot.Unknown, restore)
    {
        FirmwareRecovery = restore
            ? new(CpuTuningRecoveryState.RestoreReady,
                CpuTuningFeatures.PowerLimit | CpuTuningFeatures.CurveOptimizer) : null,
    };

    [Fact]
    public void ObservedCapabilitiesRemainVisibleButOptInDoesNotAdmitWrites()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ObservedOnly), Capabilities(DeviceFeatureAccess.ObservedOnly));
        vm.UseInstalledBackend = true;
        vm.ManagePower = true;
        Assert.True(vm.IsVisible);
        Assert.True(vm.CanRefresh);
        Assert.False(vm.CanEdit);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.RestoreCommand.CanExecute(null));
        Assert.Contains("unknown", vm.LiveSummary);
        Assert.Contains("unknown", vm.PendingBootSummary);
    }

    [Fact]
    public void RefreshPreservesEditorIntentAndNeverUsesSavedAsLive()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true)
        { ManagePower = true, PowerLimitWatts = 80, ManageCurve = true, CurveValue = -23, CurveEnabled = false };
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite), Capabilities(DeviceFeatureAccess.ReadWrite));
        Assert.Equal(80, vm.PowerLimitWatts);
        Assert.Equal(-23, vm.CurveValue);
        Assert.False(vm.CurveEnabled);
        Assert.Equal(0, vm.Settings.CurveOptimizer!.EffectiveValue);
        Assert.Contains("65", vm.SavedSummary);
        Assert.Contains("unknown", vm.LiveSummary);
        vm.CurveEnabled = true;
        Assert.Equal(-23, vm.Settings.CurveOptimizer.EffectiveValue);
    }

    [Fact]
    public void SafetyAndActualRestorePendingGateActions()
    {
        bool safe = true;
        var vm = new CpuTuningViewModel(new Client(), () => safe, () => true) { ManagePower = true };
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite), Capabilities(DeviceFeatureAccess.ReadWrite));
        Assert.True(vm.CanApply);
        Assert.False(vm.CanRestore);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true), Capabilities(DeviceFeatureAccess.ReadWrite));
        Assert.True(vm.CanRestore);
        safe = false;
        vm.NotifyState();
        Assert.False(vm.CanApply);
        Assert.False(vm.CanRestore);
    }

    [Fact]
    public void RestoreRequiresAdmissionForEveryOwnedFeature()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true),
            Capabilities(DeviceFeatureAccess.ObservedOnly));
        Assert.True(vm.BackendReadWriteAvailable);
        Assert.False(vm.CanRestore);
        Assert.False(vm.RestoreCommand.CanExecute(null));
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true),
            Capabilities(DeviceFeatureAccess.ReadWrite) with
            {
                CpuVoltageOptimizer = new(new(-30, -1, 1), DeviceFeatureAccess.ReadOnly),
            });
        Assert.False(vm.CanRestore);
        // Restore admission cannot be widened by leaving an editor field unmanaged.
        vm.ManageCurve = false;
        Assert.False(vm.CanRestore);
    }

    [Fact]
    public void CurveOnlyRecoveryDoesNotRequireUnownedPowerAdmission()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true) with
        {
            FirmwareRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer),
        }, Capabilities(DeviceFeatureAccess.ReadWrite) with
        {
            CpuPowerLimit = new(new(45, 95, 1), DeviceFeatureAccess.ObservedOnly),
        });
        Assert.True(vm.CanRestore);
        vm.ManagePower = true;
        Assert.True(vm.CanRestore);
    }

    [Fact]
    public void SavedRecoveryNeverEnablesLiveRestore()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true), Capabilities(DeviceFeatureAccess.ReadWrite));
        Assert.True(vm.CanRestore);
        vm.LiveNow = true;
        Assert.False(vm.CanRestore);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true) with
        {
            LiveRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer),
        }, Capabilities(DeviceFeatureAccess.ReadWrite));
        Assert.True(vm.CanRestore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CpuTuningRecoveryState.Unknown)]
    [InlineData(CpuTuningRecoveryState.None)]
    [InlineData(CpuTuningRecoveryState.Blocked)]
    public void GlobalPendingWithoutTargetRecoveryAuthorityCannotRestore(CpuTuningRecoveryState? state)
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true) with
        {
            FirmwareRecovery = state.HasValue ? new(state.Value, CpuTuningFeatures.None) : null,
        }, Capabilities(DeviceFeatureAccess.ReadWrite));
        Assert.False(vm.CanRestore);
    }

    [Fact]
    public void BlockedRecoveryCannotBeBypassedWithEditorOrTargetChanges()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true) { ManageCurve = true };
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true) with
        {
            FirmwareRecovery = new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer),
        }, Capabilities(DeviceFeatureAccess.ReadWrite));
        Assert.False(vm.CanApply);
        Assert.False(vm.CanRestore);
        vm.LiveNow = true;
        Assert.False(vm.CanApply);
        Assert.False(vm.CanRestore);
    }

    [Fact]
    public void PreparedCleanupWithNoHardwareCommandsRequiresReadyRecoveryAndSafety()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite, restore: true) with
        {
            FirmwareRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.None),
        }, Capabilities(DeviceFeatureAccess.ObservedOnly));
        Assert.True(vm.CanRestore);
    }

    [Fact]
    public void MeasuredMixedCurveAndUnknownPowerStayDistinct()
    {
        var vm = new CpuTuningViewModel(new Client(), () => true, () => true);
        vm.UpdateStatus(Status(DeviceFeatureAccess.ObservedOnly) with
        {
            LiveState = new(null, [new(0, -10), new(1, -11)]),
            PendingBoot = CpuTuningPendingBoot.Requested,
        }, Capabilities(DeviceFeatureAccess.ObservedOnly));
        Assert.Contains("PPT unknown", vm.LiveSummary);
        Assert.Contains("mixed", vm.LiveSummary);
        Assert.Contains("requested", vm.PendingBootSummary);
    }

    [Fact]
    public async Task TypedActionsUseTargetAndLeaveUncheckedFeaturesUnmanaged()
    {
        var client = new Client();
        var vm = new CpuTuningViewModel(client, () => true, () => true) { ManageCurve = true, CurveEnabled = true };
        vm.UpdateStatus(Status(DeviceFeatureAccess.ReadWrite), Capabilities(DeviceFeatureAccess.ReadWrite));
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(CpuTuningTarget.FirmwareNextBoot, client.Target);
        Assert.Null(client.Settings!.PowerLimitWatts);
        vm.LiveNow = true;
        Assert.Equal("Apply now", vm.ApplyLabel);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(CpuTuningTarget.LiveNow, client.Target);
        vm.UseInstalledBackend = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(client.InstalledOptIn);
    }

    private sealed class Client : OpenBlade.Testing.UnavailableServiceClient, IServiceClient
    {
        public CpuTuningTarget Target { get; private set; }
        public CpuTuningSettings? Settings { get; private set; }
        public bool InstalledOptIn { get; private set; }
        public Task<ControlOperationResult> SetCpuTuningAsync(CpuTuningTarget target, CpuTuningSettings settings, CancellationToken token)
        { Target = target; Settings = settings; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked, "test")); }
        public Task<ControlOperationResult> ObserveCpuTuningAsync(bool useInstalledBackend, CancellationToken token)
        { InstalledOptIn = useInstalledBackend; return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "test")); }
    }
}
