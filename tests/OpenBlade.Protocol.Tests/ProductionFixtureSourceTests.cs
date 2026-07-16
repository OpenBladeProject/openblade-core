using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionFixtureSourceTests
{
    [Fact]
    public void PreAdmissionEmbeddedResourceIsAbsentAndReturnsNoBytes()
    {
        ReadOnlyMemory<byte>? fixture = ProductionFixtureSource.Read();

        Assert.Null(fixture);
        Assert.DoesNotContain(
            ProductionFixtureSource.ResourceName,
            typeof(ProductionFixtureSource).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void ProductionReadEvidenceStartsEmptyAndAcceptsNoRuntimeInput()
    {
        Assert.False(ProductionBatteryReadEvidence.Production.SupportsTemporaryFullChargeQuery);
        Assert.Empty(typeof(ProductionBatteryReadEvidence).GetConstructors());
        Assert.DoesNotContain(
            typeof(ProductionBatteryReadEvidence).GetMethods(),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string)
                || parameter.ParameterType == typeof(byte[])
                || parameter.ParameterType == typeof(ReadOnlyMemory<byte>)));
    }
}
