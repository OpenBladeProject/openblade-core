using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task RustFanParity_ConfirmedManualWriteOwnsHandbackWithoutClearingIntent()
    {
        var (context, store, calls) = await CreateRustFanParityReplayAsync();
        var manual = new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default);
        context.Device.AfterSetter = subsystem =>
        {
            Assert.Equal(DeviceSubsystem.Fans, subsystem);
            Assert.Equal(manual, store.LoadAsync(default).GetAwaiter().GetResult().Overrides.FanControl);
            Assert.Equal(FanMode.Automatic, context.Runtime.GetStatus().ConfirmedState?.Fans);
            calls.Add("SetFans");
            // The exact model reports manual topology without the requested RPM or subtype.
            context.Device.ConfirmedState = context.Device.ConfirmedState with { Fans = FanMode.Maximum };
        };

        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new(FanMode.Fixed, OverrideLifetime.Persistent), manual, default);

        Assert.Equal(ControlOperationStatus.Applied, entered.Status);
        Assert.Equal(["ReadThermal", "SetFans", "ReadThermal"], calls);
        Assert.Equal(manual, Assert.Single(context.Device.FanControlWrites));
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PerformancePreset.Performance, status.ConfirmedState?.Performance);
        Assert.Equal(FanMode.Maximum, status.ConfirmedState?.Fans);
        Assert.Equal(manual, status.DesiredState.FanControl);
        Assert.Equal(ExactFanControlOwnershipDisposition.OwnedExactIntent,
            context.Runtime.FanControlOwnershipDisposition);

        calls.Clear();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                calls.Add("SetFans");
            }
        };
        ControlOperationResult handback = await context.Runtime.PrepareForShutdownAsync(default);

        Assert.Equal(ControlOperationStatus.Applied, handback.Status);
        Assert.Equal(["ReadThermal", "SetFans", "ReadThermal"], calls);
        Assert.Equal([FanMode.Fixed, FanMode.Automatic], context.Device.FanWrites);
        Assert.Empty(context.Device.PerformanceWrites);
        status = context.Runtime.GetStatus();
        Assert.Equal(PerformancePreset.Performance, status.ConfirmedState?.Performance);
        Assert.Equal(FanMode.Automatic, status.ConfirmedState?.Fans);
        Assert.Equal(manual, status.DesiredState.FanControl);
        Assert.Equal(manual, (await store.LoadAsync(default)).Overrides.FanControl);
        Assert.Equal(ExactFanControlOwnershipDisposition.Unowned,
            context.Runtime.FanControlOwnershipDisposition);
    }

    [Theory]
    [InlineData(FanMode.Automatic)]
    [InlineData(FanMode.Maximum)]
    public async Task RustFanParity_FirstObservationDoesNotGrantHandback(FanMode initialTopology)
    {
        var (context, _, _) = await CreateRustFanParityReplayAsync(initialTopology);
        Assert.Equal(
            initialTopology == FanMode.Automatic
                ? ExactFanControlOwnershipDisposition.Unowned
                : ExactFanControlOwnershipDisposition.ManualTopologyOnly,
            context.Runtime.FanControlOwnershipDisposition);

        await context.Runtime.PrepareForShutdownAsync(default);

        Assert.Empty(context.Device.FanControlWrites);
        Assert.Empty(context.Device.FanWrites);
        Assert.Equal(initialTopology, context.Runtime.GetStatus().ConfirmedState?.Fans);
    }

    private async Task<(TestContext Context, PersistentOverrideStore Store, List<string> Calls)>
        CreateRustFanParityReplayAsync(FanMode initialTopology = FanMode.Automatic)
    {
        string overridePath = Path.Combine(directory, "rust-fan-parity-overrides.json");
        TestContext context = CreateContext(
            providerAvailable: true, persistentOverridePath: overridePath, initialize: false);
        Assert.Equal("RZ09-0581|1532:02E0", context.Provider.LaptopControlRecoveryIdentity);
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsDirectManualFanTransitions = true,
        };
        BatteryConfiguration configuration = await context.Store.LoadAsync(default);
        ProfileSet profiles = configuration.Profiles;
        FanControlSettings profileFans = initialTopology == FanMode.Automatic
            ? FanControlSettings.FromLegacy(FanMode.Automatic)
            : new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default);
        PowerProfile profile = profiles.PluggedIn with
        {
            Performance = PerformancePreset.Performance,
            Fans = profileFans.Mode,
            FanControl = profileFans,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            profile.Performance, initialTopology, ConfirmedBattery(80), profile.Lighting);
        context.Runtime.Initialize(
            configuration with { Profiles = profiles with { PluggedIn = profile } },
            PowerSource.PluggedIn, PersistentOverrideState.Empty, recoveringUnexpectedExit: false);
        await context.Runtime.ObserveAsync(default);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.True(status.DeviceVerified);
        Assert.Equal(PowerSource.PluggedIn, status.ActivePowerSource);
        Assert.Equal(PerformancePreset.Performance, status.ConfirmedState?.Performance);
        Assert.Equal(initialTopology, status.ConfirmedState?.Fans);
        Assert.Equal(profileFans, status.DesiredState.FanControl);

        List<string> calls = [];
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.BeforePerformanceFanStateReadResult = _ => calls.Add("ReadThermal");
        return (context, new PersistentOverrideStore(overridePath), calls);
    }
}
