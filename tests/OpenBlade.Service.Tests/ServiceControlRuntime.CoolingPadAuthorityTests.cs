using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
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
    public async Task CoolingPadUnchangedMaintenancePreservesThePublishedSnapshotInstance()
    {
        var (context, _) = await CreateActiveLifecyclePadContextAsync(new LifecyclePad());
        ServiceStatusSnapshot baseline = context.Runtime.GetStatus();
        for (int i = 0; i < 3; i++) { await context.Runtime.MaintainCoolingPadAsync(default); }
        Assert.Same(baseline, context.Runtime.GetStatus());
    }
}
