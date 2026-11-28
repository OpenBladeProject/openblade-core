using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(OverrideLifetime.Persistent)]
    [InlineData(OverrideLifetime.UntilPowerSourceChange)]
    public async Task RustPerformanceParity_AppliesScopedIntentAndConfirmsBeforePublication(
        OverrideLifetime lifetime)
    {
        var (context, store, calls) = await CreateRustPerformanceParityReplayAsync();
        var request = new ManualOverride<PerformancePreset>(PerformancePreset.Performance, lifetime);
        PerformancePreset? persisted = lifetime == OverrideLifetime.Persistent
            ? PerformancePreset.Performance
            : null;
        context.Device.AfterSetter = subsystem =>
        {
            Assert.Equal(DeviceSubsystem.Performance, subsystem);
            Assert.Equal(persisted,
                store.LoadAsync(default).GetAwaiter().GetResult().Overrides.Performance);
            Assert.Equal(PerformancePreset.Balanced,
                context.Runtime.GetStatus().ConfirmedState?.Performance);
            calls.Add("SetPerformance");
        };

        ControlOperationResult applied = await context.Runtime.SetPerformanceOverrideAsync(request, default);

        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.Equal(["ReadThermal", "SetPerformance", "ReadThermal"], calls);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PerformancePreset.Performance, status.ConfirmedState?.Performance);
        Assert.Equal(FanMode.Automatic, status.ConfirmedState?.Fans);
        Assert.Equal(PerformancePreset.Performance, status.DesiredState.Performance.Value);
        Assert.Equal(lifetime, status.PerformanceOverride?.Lifetime);
        Assert.Empty(status.UnconfirmedWrites);
        Assert.Equal(persisted, (await store.LoadAsync(default)).Overrides.Performance);
        Assert.Equal(PerformancePreset.Balanced,
            (await context.Store.LoadAsync(default)).Profiles.PluggedIn.Performance);

        calls.Clear();
        ControlOperationResult unchanged = await context.Runtime.SetPerformanceOverrideAsync(request, default);
        Assert.Equal(ControlOperationStatus.Applied, unchanged.Status);
        Assert.Equal(["ReadThermal"], calls);
        Assert.Equal([PerformancePreset.Performance], context.Device.PerformanceWrites);
        Assert.Equal(1, context.Device.TotalSetterCalls);

        calls.Clear();
        context.Device.AfterSetter = subsystem =>
        {
            Assert.Equal(DeviceSubsystem.Performance, subsystem);
            Assert.Null(store.LoadAsync(default).GetAwaiter().GetResult().Overrides.Performance);
            Assert.Equal(PerformancePreset.Performance,
                context.Runtime.GetStatus().ConfirmedState?.Performance);
            calls.Add("SetPerformance");
        };
        ControlOperationResult cleared = await context.Runtime.SetPerformanceOverrideAsync(null, default);
        Assert.Equal(ControlOperationStatus.Applied, cleared.Status);
        Assert.Equal(["ReadThermal", "SetPerformance", "ReadThermal"], calls);
        status = context.Runtime.GetStatus();
        Assert.Null(status.PerformanceOverride);
        Assert.Equal(PerformancePreset.Balanced, status.ConfirmedState?.Performance);
        Assert.Equal(PerformancePreset.Balanced, status.DesiredState.Performance.Value);
        Assert.Equal([PerformancePreset.Performance, PerformancePreset.Balanced],
            context.Device.PerformanceWrites);
        Assert.Equal(2, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task RustPerformanceParity_UnreadableAcknowledgementSettlesByObservationWithoutReplay()
    {
        var (context, store, calls) = await CreateRustPerformanceParityReplayAsync();
        context.Device.MutatePerformanceBeforeQueuedFailure = true;
        context.Device.BeforeQueuedPerformanceFailure = () =>
        {
            Assert.Equal(PerformancePreset.Performance,
                store.LoadAsync(default).GetAwaiter().GetResult().Overrides.Performance);
            calls.Add("SetPerformance");
            int firstReadback = context.Device.PerformanceFanStateReadCalls + 1;
            context.Device.PerformanceFanStateReadFailureCalls.UnionWith(
                [firstReadback, firstReadback + 1]);
        };
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.Indeterminate, "The fake applied the preset and lost the acknowledgement."));
        var request = new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance, OverrideLifetime.Persistent);

        ControlOperationResult failed = await context.Runtime.SetPerformanceOverrideAsync(request, default);

        Assert.Equal(ControlOperationStatus.Failed, failed.Status);
        Assert.Equal(["ReadThermal", "SetPerformance", "ReadThermal"], calls);
        ServiceStatusSnapshot pending = context.Runtime.GetStatus();
        Assert.Equal([DeviceSubsystem.Performance], pending.UnconfirmedWrites);
        Assert.Equal(PerformancePreset.Balanced, pending.ConfirmedState?.Performance);
        Assert.Equal(PerformancePreset.Performance, pending.DesiredState.Performance.Value);
        Assert.Equal(PerformancePreset.Performance, pending.PerformanceOverride?.Value);
        Assert.Equal(PerformancePreset.Performance,
            (await store.LoadAsync(default)).Overrides.Performance);

        calls.Clear();
        ControlOperationResult blocked = await context.Runtime.SetPerformanceOverrideAsync(null, default);
        Assert.Equal(ControlOperationStatus.Blocked, blocked.Status);
        Assert.Empty(calls);

        context.Device.AfterReadState = () => calls.Add("ObserveState");
        await context.Runtime.ObserveAsync(default);

        Assert.Equal(["ObserveState"], calls);
        ServiceStatusSnapshot reconciled = context.Runtime.GetStatus();
        Assert.Empty(reconciled.UnconfirmedWrites);
        Assert.Empty(reconciled.ExternalChanges);
        Assert.Equal(PerformancePreset.Performance, reconciled.ConfirmedState?.Performance);
        Assert.Equal(PerformancePreset.Performance, reconciled.DesiredState.Performance.Value);
        Assert.Equal(PerformancePreset.Performance,
            (await store.LoadAsync(default)).Overrides.Performance);
        Assert.Equal([PerformancePreset.Performance], context.Device.PerformanceWrites);
        Assert.Equal(1, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task RustPerformanceParity_CancellationAfterPersistenceDoesNotSubmit()
    {
        var (context, store, calls) = await CreateRustPerformanceParityReplayAsync();
        using var cancellation = new CancellationTokenSource();
        context.Device.BeforePerformanceFanStateReadResult = _ =>
        {
            Assert.Equal(PerformancePreset.Performance,
                store.LoadAsync(default).GetAwaiter().GetResult().Overrides.Performance);
            calls.Add("ReadThermal");
            cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Runtime.SetPerformanceOverrideAsync(
                new(PerformancePreset.Performance, OverrideLifetime.Persistent),
                cancellation.Token));

        Assert.Equal(["ReadThermal"], calls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(PerformancePreset.Balanced, context.Runtime.GetStatus().ConfirmedState?.Performance);
        Assert.Equal(PerformancePreset.Performance,
            (await store.LoadAsync(default)).Overrides.Performance);

        Assert.Equal(PerformancePreset.Balanced, context.Runtime.GetStatus().DesiredState.Performance.Value);
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(PerformancePreset.Performance, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.Equal(PerformancePreset.Balanced, context.Runtime.GetStatus().ConfirmedState?.Performance);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    private async Task<(TestContext Context, PersistentOverrideStore Store, List<string> Calls)>
        CreateRustPerformanceParityReplayAsync()
    {
        string overridePath = Path.Combine(directory, "rust-performance-parity-overrides.json");
        TestContext context = CreateContext(
            providerAvailable: true, persistentOverridePath: overridePath);
        Assert.Equal("RZ09-0581|1532:02E0", context.Provider.LaptopControlRecoveryIdentity);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            baseline.Performance, baseline.Fans, ConfirmedBattery(80), baseline.Lighting);
        await context.Runtime.ObserveAsync(default);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Balanced, context.Runtime.GetStatus().ConfirmedState?.Performance);
        Assert.Equal(FanMode.Automatic, context.Runtime.GetStatus().ConfirmedState?.Fans);

        List<string> calls = [];
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.BeforePerformanceFanStateReadResult = _ => calls.Add("ReadThermal");
        return (context, new PersistentOverrideStore(overridePath), calls);
    }
}
