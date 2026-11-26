using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task CoolingPadControlDoesNotRequireAnAdmittedLaptop()
    {
        var pad = new LifecyclePad();
        var (context, user) = await CreateActiveLifecyclePadContextAsync(pad, laptopAvailable: false);
        Assert.False(context.Provider.Available);
        Assert.True(context.Runtime.GetStatus().CoolingPad.Active);
        Assert.Equal(1, pad.FanWrites);

        ControlOperationResult laptop = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent), default);
        Assert.NotEqual(ControlOperationStatus.Applied, laptop.Status);
        Assert.Equal(0, context.Device.TotalSetterCalls);

        ControlOperationResult released = await context.Runtime.ReleaseCoolingPadControlAsync(user, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, released.Status);
        Assert.Equal(1, pad.RestoreCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task CoolingPadPeriodicPowerClassificationDoesNotRevokeTheWindowsSessionOwner()
    {
        var pad = new LifecyclePad();
        var (context, _) = await CreateActiveLifecyclePadContextAsync(pad);
        int applied = pad.FanWrites;
        for (int i = 0; i < 3; i++)
        {
            await context.Runtime.RefreshPowerSourceClassificationAsync(default);
            await context.Runtime.MaintainCoolingPadAsync(default);
            Assert.True(context.Runtime.GetStatus().CoolingPad.Active);
            Assert.False(context.Runtime.GetStatus().CoolingPad.WritesPaused);
        }
        Assert.Equal(applied, pad.FanWrites);
    }

    [Fact]
    public async Task CoolingPadChangeSurvivesPeriodicPowerClassificationDuringItsWrite()
    {
        var pad = new LifecyclePad();
        var (context, user) = await CreateActiveLifecyclePadContextAsync(pad);
        // The 15 s classification holds no pad gate. Without its own epoch, a tick that
        // overlapped a change refused it, sometimes after part of it reached the pad.
        pad.DuringFanWrite = () => context.Runtime.RefreshPowerSourceClassificationAsync(default);
        ControlOperationResult result = await context.Runtime.SetCoolingPadSettingsAsync(
            new CoolingPadSettings { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 2000 }, user, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(2, pad.FanWrites);
        Assert.True(context.Runtime.GetStatus().CoolingPad.Active);
        Assert.False(context.Runtime.GetStatus().CoolingPad.WritesPaused);
    }

    [Fact]
    public async Task CoolingPadChangeStopsWhenWindowsReportsAPowerSourceChangeDuringItsWrite()
    {
        var pad = new LifecyclePad();
        var (context, user) = await CreateActiveLifecyclePadContextAsync(pad);
        pad.DuringFanWrite = async () =>
        {
            pad.DuringFanWrite = null;
            await context.Runtime.HandlePowerSourceChangedAsync(default);
        };
        ControlOperationResult result = await context.Runtime.SetCoolingPadSettingsAsync(
            new CoolingPadSettings { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 2000 }, user, () => true, default);
        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, pad.FanWrites);
    }

    [Fact]
    public async Task CoolingPadUnchangedMaintenancePreservesThePublishedSnapshotInstance()
    {
        var (context, _) = await CreateActiveLifecyclePadContextAsync(new LifecyclePad());
        ServiceStatusSnapshot baseline = context.Runtime.GetStatus();
        for (int i = 0; i < 3; i++) { await context.Runtime.MaintainCoolingPadAsync(default); }
        Assert.Same(baseline, context.Runtime.GetStatus());
    }
}
