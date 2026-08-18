using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class HyperBoostFixtureSourceTests
{
    [Fact]
    public void EmbeddedFixtureRequiresExactBios400TargetAndContainsReversiblePairs()
    {
        ReadOnlyMemory<byte>? fixture = HyperBoostFixtureSource.Read();

        Assert.NotNull(fixture);
        Assert.Contains(
            HyperBoostFixtureSource.ResourceName,
            typeof(HyperBoostFixtureSource).Assembly.GetManifestResourceNames());
        var target = new DeviceIdentity("RZ09-0581", 0x1532, 0x02E0, "4.00");
        DeviceCommandCatalog catalog = CapturedFixtureFile.LoadCatalog(
            fixture!.Value,
            TrustedFixtureHashRegistry.HyperBoostProduction,
            target);

        Assert.True(ProductionBatteryPort.HasHyperBoostCommandSet(catalog));
        Assert.Throws<UnvalidatedCommandException>(() => CapturedFixtureFile.LoadCatalog(
            fixture.Value,
            TrustedFixtureHashRegistry.HyperBoostProduction,
            target with { BiosVersion = "3.01" }));
        Assert.Throws<UnvalidatedCommandException>(() => CapturedFixtureFile.LoadCatalog(
            fixture.Value,
            TrustedFixtureHashRegistry.Production,
            target));
    }
}
