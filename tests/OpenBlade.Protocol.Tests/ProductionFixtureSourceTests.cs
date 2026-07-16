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
        var expected = new Dictionary<string, (byte CommandId, byte Payload)>
        {
            [ProductionBatteryPort.Limit50Action] = (0x12, 0xB2),
            [ProductionBatteryPort.Limit55Action] = (0x12, 0xB7),
            [ProductionBatteryPort.Limit60Action] = (0x12, 0xBC),
            [ProductionBatteryPort.Limit65Action] = (0x12, 0xC1),
            [ProductionBatteryPort.Limit70Action] = (0x12, 0xC6),
            [ProductionBatteryPort.Limit75Action] = (0x12, 0xCB),
            [ProductionBatteryPort.Limit80Action] = (0x12, 0xD0),
            [ProductionBatteryPort.ProtectionOffAction] = (0x12, 0x50),
            [ProductionBatteryPort.TemporaryEnableAction] = (0x0F, 0x04),
            [ProductionBatteryPort.TemporaryCancelAction] = (0x0F, 0x00),
        };
        foreach ((string action, (byte commandId, byte payload)) in expected)
        {
            Assert.True(catalog.TryGet(action, out RazerCommandDefinition? command));
            Assert.NotNull(command);
            Assert.Equal((0x07, commandId), (command.CommandClass, command.CommandId));
            Assert.Equal([payload], command.Payload.ToArray());
        }
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
