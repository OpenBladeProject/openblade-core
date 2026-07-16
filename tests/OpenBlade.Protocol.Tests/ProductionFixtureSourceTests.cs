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
        var expected = new Dictionary<string, byte>
        {
            [ProductionBatteryPort.Limit50Action] = 0xB2,
            [ProductionBatteryPort.Limit55Action] = 0xB7,
            [ProductionBatteryPort.Limit60Action] = 0xBC,
            [ProductionBatteryPort.Limit65Action] = 0xC1,
            [ProductionBatteryPort.Limit70Action] = 0xC6,
            [ProductionBatteryPort.Limit75Action] = 0xCB,
            [ProductionBatteryPort.Limit80Action] = 0xD0,
            [ProductionBatteryPort.ProtectionOffAction] = 0x50,
        };
        foreach ((string action, byte payload) in expected)
        {
            Assert.True(catalog.TryGet(action, out RazerCommandDefinition? command));
            Assert.NotNull(command);
            Assert.Equal((0x07, 0x12), (command.CommandClass, command.CommandId));
            Assert.Equal([payload], command.Payload.ToArray());
        }

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
