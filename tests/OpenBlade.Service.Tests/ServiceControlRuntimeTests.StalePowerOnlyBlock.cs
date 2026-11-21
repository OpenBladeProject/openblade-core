using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    // Conflicting adapter replies publish a power-only block. A later fresh,
    // classified power change ends it, so the destination profile's thermal step
    // is applied instead of being refused with "Thermal write authority closed"
    // and dropped. A transient read failure only relabels the state as
    // RefreshingControls; it must not turn that block into a non-power one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalePowerOnlyBlock_ClassifiedPowerChange_AppliesDestinationThermalStep(
        bool transientRead)
    {
        TestContext context = await CreateStalePowerOnlyBlockContextAsync(transientRead);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        Assert.Equal(
            transientRead
                ? DeviceObservationReason.RefreshingControls
                : DeviceObservationReason.RetainedPowerSource,
            before.ObservationReason);
        int writes = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.DoesNotContain("Thermal write authority closed", status.Message, StringComparison.Ordinal);
        Assert.Equal(PerformancePreset.BatterySaver, status.DesiredState.Performance.Value);
        Assert.Equal(PerformancePreset.BatterySaver, status.ConfirmedState?.Performance);
        Assert.Equal([PerformancePreset.BatterySaver], context.Device.PerformanceWrites.Skip(writes));
        Assert.Equal(PowerSource.Battery, status.ActivePowerSource);
        Assert.False(status.WritesBlocked);
    }

    // A block another owner holds is not released by the power change.
    [Fact]
    public async Task StalePowerOnlyBlock_ClassifiedPowerChange_KeepsNonPowerBlock()
    {
        TestContext context = await CreateStalePowerOnlyBlockContextAsync(transientRead: false);
        context.Provider.IsExperimentalWritesPaused = true;
        int writes = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.True(status.WritesBlocked);
        Assert.Equal(writes, context.Device.PerformanceWrites.Count);
        Assert.Equal(PerformancePreset.Balanced, status.ConfirmedState?.Performance);
    }

    // A transient read label over a block with no tracked power-only owner
    // stays non-power: the release requires the publish layer's tracking.
    [Fact]
    public async Task StalePowerOnlyBlock_UntrackedBlockUnderTransientRead_StaysNonPower()
    {
        TestContext context = await CreateStalePowerOnlyBlockContextAsync(transientRead: false);
        SetReviewRuntimeField(context.Runtime, "powerOnlyWriteBlockRevision", -1L);
        context.Device.ReadStateFailure = new IOException("Injected transient read failure.");
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ReadStateFailure = null;
        Assert.Equal(DeviceObservationReason.RefreshingControls, context.Runtime.GetStatus().ObservationReason);
        int writes = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Contains("Thermal write authority closed", status.Message, StringComparison.Ordinal);
        Assert.Equal(writes, context.Device.PerformanceWrites.Count);
    }

    // After the release, an ambiguous destination submission stops further
    // writes and is resolved read-only; it is never replayed.
    [Fact]
    public async Task StalePowerOnlyBlock_UnresolvedDestinationSubmission_IsNotReplayed()
    {
        TestContext context = await CreateStalePowerOnlyBlockContextAsync(transientRead: false);
        int writes = context.Device.PerformanceWrites.Count;
        context.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        context.Device.MutatePerformanceBeforeQueuedFailure = true;
        context.Device.BeforeQueuedPerformanceFailure = () =>
            context.Device.ReadStateFailure = new IOException("Injected lost readback.");

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal([PerformancePreset.BatterySaver], context.Device.PerformanceWrites.Skip(writes));
        Assert.Contains(DeviceSubsystem.Performance, status.UnconfirmedWrites);
        Assert.True(status.WritesBlocked);

        context.Device.ReadStateFailure = null;
        context.Device.BeforeQueuedPerformanceFailure = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        status = context.Runtime.GetStatus();
        Assert.Equal([PerformancePreset.BatterySaver], context.Device.PerformanceWrites.Skip(writes));
        Assert.Empty(status.UnconfirmedWrites);
        Assert.Equal(PerformancePreset.BatterySaver, status.ConfirmedState?.Performance);
    }

    private async Task<TestContext> CreateStalePowerOnlyBlockContextAsync(bool transientRead)
    {
        TestContext context = CreateWritableContext();
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot initial = context.Runtime.GetStatus();
        Assert.False(initial.WritesBlocked);
        Assert.Equal(PerformancePreset.Balanced, initial.ConfirmedState?.Performance);

        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.FullPower280W);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.UsbCPower100W);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        if (transientRead)
        {
            await InjectTransientReadFailureAsync(context);
        }

        ServiceStatusSnapshot blocked = context.Runtime.GetStatus();
        Assert.True(blocked.DeviceVerified);
        Assert.True(blocked.WritesBlocked);
        Assert.Empty(blocked.UnconfirmedWrites);
        return context;
    }

    private static async Task InjectTransientReadFailureAsync(TestContext context)
    {
        context.Device.ReadStateFailure = new IOException("Injected transient read failure.");
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ReadStateFailure = null;
        Assert.Equal(DeviceObservationReason.RefreshingControls, context.Runtime.GetStatus().ObservationReason);
    }
}
