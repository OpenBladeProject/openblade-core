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
    [InlineData("checkingConnection")]
    [InlineData("unverified")]
    public async Task PowerChange_PendingNextToAnotherWriteBlock_KeepsThatBlock(string owner)
    {
        TestContext context = CreateWritableContext();
        await CreateUnresolvedPendingPerformanceAsync(context);
        if (owner == "experimentalPaused")
        {
            context.Provider.IsExperimentalWritesPaused = true;
        }
        else if (owner == "checkingConnection")
        {
            // One missed read: the device stays verified while the connection is checked.
            context.Provider.Available = false;
            await context.Runtime.ObserveAsync(CancellationToken.None);
            Assert.True(context.Runtime.GetStatus().DeviceVerified);
            Assert.Equal(DeviceObservationReason.CheckingConnection, context.Runtime.GetStatus().ObservationReason);
            Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
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

    // A pending confirmation can exist while the device is unverified: a power
    // change without an admitted device publishes it unverified, and an explicit
    // write then loses its ACK and readback. The next power change must not
    // publish an unverified device with writes unblocked.
    [Fact]
    public async Task PowerChange_UnverifiedDeviceWithPending_StaysBlocked()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Provider.Available = false;
        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        context.Provider.Available = true;
        context.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        context.Device.MutatePerformanceBeforeQueuedFailure = true;
        context.Device.BeforeQueuedPerformanceFailure = () =>
            context.Device.ReadStateFailure = new IOException("Injected lost readback.");
        await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        context.Device.ReadStateFailure = null;
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        int published = context.Hub.Published.Count;

        context.Provider.Available = false;
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.NotEmpty(context.Hub.Published.Skip(published));
        Assert.DoesNotContain(
            context.Hub.Published.Skip(published),
            snapshot => !snapshot.DeviceVerified && !snapshot.WritesBlocked);
    }

    // A power-only block (conflicting adapter replies) has no pending confirmation
    // to release. The classified change that follows must not publish the
    // unclassified source with writes unblocked.
    [Fact]
    public async Task PowerChange_WithoutPending_KeepsAPowerOnlyBlock()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(
            profiles with { UsbC = profiles.UsbC with { Performance = PerformancePreset.Silent } },
            CancellationToken.None);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.FullPower280W);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.UsbCPower100W);
        await context.Runtime.RefreshPowerSourceClassificationAsync(default);
        ServiceStatusSnapshot blocked = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.ExternalUnknown, blocked.ActivePowerSource);
        Assert.True(blocked.WritesBlocked);
        Assert.Empty(blocked.UnconfirmedWrites);
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        int published = context.Hub.Published.Count;

        await context.Runtime.RefreshPowerSourceClassificationAsync(default);

        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.DoesNotContain(
            context.Hub.Published.Skip(published),
            snapshot => snapshot.ActivePowerSource == PowerSource.ExternalUnknown && !snapshot.WritesBlocked);
    }
}
