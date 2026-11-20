using OpenBlade.Contracts;
using OpenBlade.Protocol;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C7)]
    public async Task QueryCapableBlade18_UnknownOverrideBlocksPolicyPlanBeforeFirstRead(int product)
    {
        var io = new Io { Raw = 0xD0 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(CancellationToken.None);
        Assert.True(capabilities.SupportsTemporaryFullCharge);

        BatteryOperationPlan plan = BatteryOperationPlanner.CreateBatteryPlan(
            new DesiredBatteryState(BatteryPolicy.Create(75), true, false),
            capabilities, new ConfirmedBatteryState(true, 80, null));

        Assert.False(plan.Supported);
        Assert.IsType<BatteryOperation.None>(plan.Operation);
        Assert.Empty(io.Setters);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x02B6)]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task LimiterOnlyProvider_PreservesUnknownOverrideAndAdmitsPolicyWrites(int product)
    {
        var io = new Io { Raw = 0xD0 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState initial = await device.ReadStateAsync(CancellationToken.None);
        Assert.NotNull(initial.Battery);
        Assert.Null(initial.Battery.TemporaryFullCharge);

        ScopedApplyOutcome changed = await new ControlCoordinator(device).ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75), CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(BatteryConfirmationScope.PolicyOnly, changed.BatteryConfirmation);
        Assert.Equal(75, changed.Readback!.Battery!.ChargeLimitPercent);
        Assert.Null(changed.Readback.Battery.TemporaryFullCharge);
        Assert.Equal(new byte[] { 0xCB }, io.Setters);
        Assert.DoesNotContain(io.Commands, command => command is (0x07, 0x8F));
    }

    // RZ09-0529 admits temporary full charge from its own product 711 sources, so the
    // service reads the real override and checks it again before a policy write.
    [Fact]
    public async Task Rz090529Provider_ReadsOverrideAndChecksItBeforePolicyWrite()
    {
        var io = new Io { Raw = 0xD0 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = 0x02C7 }, productId: 0x02C7);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.True((await device.ReadCapabilitiesAsync(CancellationToken.None)).SupportsTemporaryFullCharge);
        ConfirmedDeviceState initial = await device.ReadStateAsync(CancellationToken.None);
        Assert.False(initial.Battery!.TemporaryFullCharge);
        int beforeWrite = io.Commands.Count;

        ScopedApplyOutcome changed = await new ControlCoordinator(device).ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75), CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(75, changed.Readback!.Battery!.ChargeLimitPercent);
        Assert.Equal(new byte[] { 0xCB }, io.Setters);
        var write = io.Commands.Skip(beforeWrite).ToList();
        int setter = write.IndexOf((0x07, 0x12));
        Assert.InRange(write.IndexOf((0x07, 0x8F)), 0, setter - 1);
    }
}
