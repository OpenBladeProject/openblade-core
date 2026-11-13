using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public async Task ExternalPower_ExplicitHyperBoostRecoveryRestoresExactSupportedIntent(PowerSource source)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = CreateRestorePendingJournal(FanControlSettings.Default,
                new HyperBoostRecoveryBaseline(PerformancePreset.Balanced,
                    FanMode.Automatic, HyperBoostActive: false)),
        };
        TestContext context = CreateContext(true, initialPowerSource: source,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(
                CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.RecoverHyperBoostRestoreAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(source, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }
}
