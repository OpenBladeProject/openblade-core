using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The power-change release discharges only the write block that the abandoned
// pending confirmations held. A block another owner holds, and the block of an
// unverified device, stay published.
public sealed partial class ServiceControlRuntimeTests
{
    private static async Task CreateUnresolvedPendingPerformanceAsync(TestContext context)
    {
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        context.Device.MutatePerformanceBeforeQueuedFailure = true;
        context.Device.BeforeQueuedPerformanceFailure = () =>
            context.Device.ReadStateFailure = new IOException("Injected lost readback.");
        await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        context.Device.ReadStateFailure = null;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = null };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
    }

    [Theory]
    [InlineData("experimentalPaused")]
    [InlineData("unverified")]
    public async Task PowerChange_PendingNextToAnotherWriteBlock_KeepsThatBlock(string owner)
    {
        TestContext context = CreateWritableContext();
        await CreateUnresolvedPendingPerformanceAsync(context);
        if (owner == "experimentalPaused")
        {
            context.Provider.IsExperimentalWritesPaused = true;
        }
        else
        {
            context.Provider.Available = false;
            // Bounded retries keep the last presentation through early misses.
            for (int miss = 0; miss < 5 && context.Runtime.GetStatus().DeviceVerified; miss++)
            {
                await context.Runtime.ObserveAsync(CancellationToken.None);
            }
            Assert.False(context.Runtime.GetStatus().DeviceVerified);
            Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        }
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        int published = context.Hub.Published.Count;
        int performanceWrites = context.Device.PerformanceSetCalls;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.All(context.Hub.Published.Skip(published), snapshot => Assert.True(snapshot.WritesBlocked));
        Assert.Equal(performanceWrites, context.Device.PerformanceSetCalls);
    }
}
