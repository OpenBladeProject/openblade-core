using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task LocalDimming_MiniLedPanelReadsStateAndClearsThroughTheProvider(int productId)
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x0C };
        var native = new Native { ProductId = (ushort)productId };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: productId, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(CancellationToken.None);
        Assert.Equal(new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true), capabilities.LocalDimming);
        Assert.True((await device.ReadStateAsync(CancellationToken.None)).LocalDimmingActive);

        await device.ClearLocalDimmingAsync(CancellationToken.None);

        // Whole-byte write keeps temporary full charge (0x04) and drops only local dimming.
        Assert.Equal([0x04], io.PowerModeControlWrites);
        Assert.False((await device.ReadStateAsync(CancellationToken.None)).LocalDimmingActive);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    [Fact]
    public async Task LocalDimming_PanelWithoutMiniLedNeverReadsOrWritesPowerModeControlForDimming()
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x00, PowerModeControl = 0x08 };
        var native = new Native { ProductId = 0x02B7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: 0x02B7, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        Assert.Null((await device.ReadCapabilitiesAsync(CancellationToken.None)).LocalDimming);
        Assert.Null((await device.ReadStateAsync(CancellationToken.None)).LocalDimmingActive);
        await Assert.ThrowsAnyAsync<Exception>(() => device.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Empty(io.PowerModeControlWrites);
        Assert.Contains(io.Commands, command => command is (0x0D, 0x8F));
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    public async Task LocalDimming_OtherModelsNeverReadThePanel(int productId)
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x08 };
        var native = new Native { ProductId = (ushort)productId };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native, productId: productId);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(CancellationToken.None);
        Assert.False(capabilities.LocalDimming?.SupportsClear ?? false);
        Assert.Null((await device.ReadStateAsync(CancellationToken.None)).LocalDimmingActive);

        Assert.DoesNotContain(io.Commands, command => command is (0x0D, 0x8F));
        Assert.Empty(io.PowerModeControlWrites);
    }
}
