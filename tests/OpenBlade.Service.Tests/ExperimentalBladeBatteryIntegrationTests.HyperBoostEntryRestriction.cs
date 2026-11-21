using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

// The real factory and port behind the real serialized provider; only HID and
// host identity are faked.
public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    private sealed class MutableSkuIdentity(string productName) : IHostIdentitySource
    {
        internal string? Sku { get; set; }

        public HostIdentityReadResult ReadExactIdentity() =>
            new(new HostIdentityEvidence("Razer", productName, Sku, "test"), null);
    }

    private static (WindowsVerifiedBladeDeviceProvider Provider, IBladeDevice Device) OpenRealHyperBoostProvider(
        int productId,
        Io io,
        MutableSkuIdentity identity,
        PowerSource power)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var native = new Native { ProductId = (ushort)productId };
        var factory = new ExperimentalBladeFactory(model, identity, native, new Guard(), io, () => power);
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready, BatteryValidationFirmwareCompatibility.ModelScoped),
            factory, UnavailableFanRpmFactory(), factory.CreateLightingFactory());
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        return (provider, device);
    }

    [Theory]
    [InlineData(0x02B7, "RZ09-0510TEH3", "RZ09-0510SEH3", true)]
    [InlineData(0x02B7, "RZ09-0510TEH3", null, false)]
    [InlineData(0x029F, "RZ09-0483TEH3", "RZ09-0483SEH3", true)]
    [InlineData(0x029F, "RZ09-0483TEH3", null, false)]
    public async Task RealProvider_SkuReadLaterDeclinesEntryButNeverExit(
        int productId, string admitted, string? later, bool restricted)
    {
        var io = new Io { Raw = 0x50, Mode = 7 };
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var identity = new MutableSkuIdentity(model.ProductName) { Sku = admitted };
        (WindowsVerifiedBladeDeviceProvider provider, IBladeDevice device) =
            OpenRealHyperBoostProvider(productId, io, identity, PowerSource.PluggedIn);
        using (provider)
        {
            DeviceCapabilities open = await device.ReadCapabilitiesAsync(CancellationToken.None);
            Assert.Equal(DeviceFeatureAccess.ReadWrite, open.HyperBoostAccess);
            Assert.Null(open.HyperBoostEntryRestriction);
            Assert.True((await device.ReadStateAsync(CancellationToken.None)).HyperBoostActive);

            identity.Sku = later;
            DeviceCapabilities reread = await device.ReadCapabilitiesAsync(CancellationToken.None);
            Assert.Equal(DeviceFeatureAccess.ReadWrite, reread.HyperBoostAccess);
            Assert.Equal(restricted ? HyperBoostEntryRestriction.VendorSystemSku : null, reread.HyperBoostEntryRestriction);
            int setsBefore = io.Commands.Count(command => command is (0x0D, 0x02));

            await device.SetHyperBoostAsync(false, PerformancePreset.Balanced, CancellationToken.None);

            // One 0D/02 write per thermal zone.
            Assert.Equal(setsBefore + 2, io.Commands.Count(command => command is (0x0D, 0x02)));
            Assert.Equal(0, io.Mode);
            Assert.False((await device.ReadStateAsync(CancellationToken.None)).HyperBoostActive);
            Assert.False(provider.IsExperimentalWritesPaused);

            int setsAfterExit = io.Commands.Count(command => command is (0x0D, 0x02));
            Exception? entry = await Record.ExceptionAsync(() =>
                device.SetHyperBoostAsync(true, PerformancePreset.Balanced, CancellationToken.None));
            if (restricted)
            {
                Assert.NotNull(entry);
                Assert.Equal(HyperBoostSystemSkuPolicy.EntryRestrictedMessage, entry.Message);
                Assert.Equal(setsAfterExit, io.Commands.Count(command => command is (0x0D, 0x02)));
                Assert.Equal(0, io.Mode);
            }
            else
            {
                // An unreadable SKU admits entry, as in Synapse; later entry gates still apply.
                Assert.NotEqual(HyperBoostSystemSkuPolicy.EntryRestrictedMessage, entry?.Message);
            }
        }
    }

    [Theory]
    [InlineData(0x02B7, PerformancePreset.Turbo, PowerSource.PluggedIn, 1)]
    [InlineData(0x02B7, PerformancePreset.Silent, PowerSource.PluggedIn, 5)]
    [InlineData(0x029F, PerformancePreset.Performance, PowerSource.PluggedIn, 2)]
    public async Task RealProvider_ExitFromManualFansRestoresTheBase(
        int productId, PerformancePreset baseMode, PowerSource power, byte expectedMode)
    {
        // Synapse can leave mode 7 with manual fans; its exit writes the base with automatic
        // fans. The battery handoff is covered at the port, where the adapter reading is modeled.
        var io = new Io { Raw = 0x50, Mode = 7, Fan = 1 };
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var identity = new MutableSkuIdentity(model.ProductName) { Sku = null };
        (WindowsVerifiedBladeDeviceProvider provider, IBladeDevice device) =
            OpenRealHyperBoostProvider(productId, io, identity, power);
        using (provider)
        {
            ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
            Assert.True(state.HyperBoostActive);
            Assert.Equal(FanMode.Fixed, state.Fans);

            await device.SetHyperBoostAsync(false, baseMode, CancellationToken.None);

            Assert.Equal(expectedMode, io.Mode);
            Assert.Equal(0, io.Fan);
            ConfirmedDeviceState after = await device.ReadStateAsync(CancellationToken.None);
            Assert.False(after.HyperBoostActive);
            Assert.Equal(FanMode.Automatic, after.Fans);
            Assert.False(provider.IsExperimentalWritesPaused);
        }
    }
}
