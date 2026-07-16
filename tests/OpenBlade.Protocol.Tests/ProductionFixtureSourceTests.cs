using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionFixtureSourceTests
{
    [Fact]
    public void ProductionEmbeddedResourceIsTrustedAndExposesOnlyAdmittedBatteryActions()
    {
        ReadOnlyMemory<byte>? fixture = ProductionFixtureSource.Read();

        Assert.NotNull(fixture);
        Assert.Contains(
            ProductionFixtureSource.ResourceName,
            typeof(ProductionFixtureSource).Assembly.GetManifestResourceNames());
        DeviceCommandCatalog catalog = CapturedFixtureFile.LoadCatalog(
            fixture.Value,
            TrustedFixtureHashRegistry.Production);
        Assert.True(catalog.TryGet(ProductionBatteryPort.Limit80Action, out _));
        Assert.True(catalog.TryGet(ProductionBatteryPort.ProtectionOffAction, out _));
        Assert.False(catalog.TryGet(ProductionBatteryPort.TemporaryEnableAction, out _));
        Assert.False(catalog.TryGet(ProductionBatteryPort.TemporaryCancelAction, out _));
    }

    [Fact]
    public void ProductionReadEvidenceIsCodeControlledAndAcceptsNoRuntimeInput()
    {
        Assert.True(ProductionBatteryReadEvidence.Production.SupportsTemporaryFullChargeQuery);
        Assert.Empty(typeof(ProductionBatteryReadEvidence).GetConstructors());
        Assert.DoesNotContain(
            typeof(ProductionBatteryReadEvidence).GetMethods(),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string)
                || parameter.ParameterType == typeof(byte[])
                || parameter.ParameterType == typeof(ReadOnlyMemory<byte>)));
    }
}
