using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionFixtureSourceTests
{
    [Fact]
    public void ProductionEmbeddedResourceIsTrustedAndExposesOnlyExactAdmittedActions()
    {
        ReadOnlyMemory<byte>? fixture = ProductionFixtureSource.Read();

        Assert.NotNull(fixture);
        Assert.Contains(
            ProductionFixtureSource.ResourceName,
            typeof(ProductionFixtureSource).Assembly.GetManifestResourceNames());
        DeviceCommandCatalog catalog = CapturedFixtureFile.LoadCatalog(
            fixture.Value,
            TrustedFixtureHashRegistry.Production);
        var expected = new Dictionary<string, (byte ClassId, byte CommandId, byte[] Payload)>
        {
            [ProductionBatteryPort.Limit50Action] = (0x07, 0x12, [0xB2]),
            [ProductionBatteryPort.Limit55Action] = (0x07, 0x12, [0xB7]),
            [ProductionBatteryPort.Limit60Action] = (0x07, 0x12, [0xBC]),
            [ProductionBatteryPort.Limit65Action] = (0x07, 0x12, [0xC1]),
            [ProductionBatteryPort.Limit70Action] = (0x07, 0x12, [0xC6]),
            [ProductionBatteryPort.Limit75Action] = (0x07, 0x12, [0xCB]),
            [ProductionBatteryPort.Limit80Action] = (0x07, 0x12, [0xD0]),
            [ProductionBatteryPort.ProtectionOffAction] = (0x07, 0x12, [0x50]),
            [ProductionBatteryPort.TemporaryEnableAction] = (0x07, 0x0F, [0x04]),
            [ProductionBatteryPort.TemporaryCancelAction] = (0x07, 0x0F, [0x00]),
            [ProductionBatteryPort.PerformanceBalancedFirstAction] =
                (0x0D, 0x02, [0x01, 0x01, 0x00, 0x00]),
            [ProductionBatteryPort.PerformanceBalancedSecondAction] =
                (0x0D, 0x02, [0x01, 0x02, 0x00, 0x00]),
            [ProductionBatteryPort.PerformanceSilentFirstAction] =
                (0x0D, 0x02, [0x01, 0x01, 0x05, 0x00]),
            [ProductionBatteryPort.PerformanceSilentSecondAction] =
                (0x0D, 0x02, [0x01, 0x02, 0x05, 0x00]),
            [ProductionBatteryPort.PerformanceBoostFirstAction] =
                (0x0D, 0x02, [0x01, 0x01, 0x02, 0x00]),
            [ProductionBatteryPort.PerformanceBoostSecondAction] =
                (0x0D, 0x02, [0x01, 0x02, 0x02, 0x00]),
            [ProductionBatteryPort.PerformanceBatterySaverFirstAction] =
                (0x0D, 0x02, [0x01, 0x01, 0x03, 0x00]),
            [ProductionBatteryPort.PerformanceBatterySaverSecondAction] =
                (0x0D, 0x02, [0x01, 0x02, 0x03, 0x00]),
            [ProductionBatteryPort.PerformanceBatteryBalancedFirstAction] =
                (0x0D, 0x02, [0x01, 0x01, 0x06, 0x00]),
            [ProductionBatteryPort.PerformanceBatteryBalancedSecondAction] =
                (0x0D, 0x02, [0x01, 0x02, 0x06, 0x00]),
            [ProductionBatteryPort.PerformanceCustomFirstAction] =
                (0x0D, 0x02, [0x01, 0x01, 0x04, 0x00]),
            [ProductionBatteryPort.PerformanceCustomSecondAction] =
                (0x0D, 0x02, [0x01, 0x02, 0x04, 0x00]),
            [ProductionBatteryPort.CustomCpuLowAction] =
                (0x0D, 0x07, [0x01, 0x01, 0x00]),
            [ProductionBatteryPort.CustomCpuMediumAction] =
                (0x0D, 0x07, [0x01, 0x01, 0x01]),
            [ProductionBatteryPort.CustomCpuHighAction] =
                (0x0D, 0x07, [0x01, 0x01, 0x02]),
            [ProductionBatteryPort.CustomGpuLowAction] =
                (0x0D, 0x07, [0x01, 0x02, 0x00]),
            [ProductionBatteryPort.CustomGpuMediumAction] =
                (0x0D, 0x07, [0x01, 0x02, 0x01]),
            [ProductionBatteryPort.CustomGpuHighAction] =
                (0x0D, 0x07, [0x01, 0x02, 0x02]),
            [ProductionBatteryPort.FanMaximumStateFirstAction] =
                (0x0D, 0x02, [0x01, 0x01, 0x00, 0x01]),
            [ProductionBatteryPort.FanMaximumStateSecondAction] =
                (0x0D, 0x02, [0x01, 0x02, 0x00, 0x01]),
            [ProductionBatteryPort.FanMaximumSpeedFirstAction] =
                (0x0D, 0x01, [0x01, 0x01, 0x36]),
            [ProductionBatteryPort.FanMaximumSpeedSecondAction] =
                (0x0D, 0x01, [0x01, 0x02, 0x36]),
            [ProductionBatteryPort.StartupAnimationOnAction] =
                (0x0F, 0x18, [0x00, 0x00]),
            [ProductionBatteryPort.StartupAnimationOffAction] =
                (0x0F, 0x18, [0x00, 0x01]),
            [ProductionBatteryPort.FunctionKeysPrimaryAction] =
                (0x02, 0x06, [0x01, 0x00]),
            [ProductionBatteryPort.MultimediaKeysPrimaryAction] =
                (0x02, 0x06, [0x01, 0x01]),
            [ProductionBatteryPort.GamingModeOnAction] =
                (0x00, 0x08, [0x01, 0x00, 0x00, 0x00]),
            [ProductionBatteryPort.GamingModeOffAction] =
                (0x00, 0x08, [0x00, 0x00, 0x00, 0x00]),
        };
        using JsonDocument document = JsonDocument.Parse(fixture.Value);
        string[] fixtureActions = document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("action").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal).ToArray(), fixtureActions);

        foreach ((string action, (byte classId, byte commandId, byte[] payload)) in expected)
        {
            Assert.True(catalog.TryGet(action, out RazerCommandDefinition? command));
            Assert.NotNull(command);
            Assert.Equal((classId, commandId), (command.CommandClass, command.CommandId));
            Assert.Equal(payload, command.Payload.ToArray());
        }

        foreach (string action in new[]
        {
            ProductionBatteryPort.StartupAnimationOnAction,
            ProductionBatteryPort.StartupAnimationOffAction,
            ProductionBatteryPort.FunctionKeysPrimaryAction,
            ProductionBatteryPort.MultimediaKeysPrimaryAction,
            ProductionBatteryPort.GamingModeOnAction,
            ProductionBatteryPort.GamingModeOffAction,
        })
        {
            Assert.True(catalog.TryGet(action, out RazerCommandDefinition? command));
            Assert.True(command!.OneShotOnly);
        }
        Assert.Equal(
            [0x32, 0x0A, 0x00, 0x00],
            catalogCommand(ProductionBatteryPort.GamingModeOnAction).ExpectedResponsePayload.ToArray());
        Assert.Equal(
            [0x32, 0x0A, 0x00, 0x00],
            catalogCommand(ProductionBatteryPort.GamingModeOffAction).ExpectedResponsePayload.ToArray());

        DeviceCapabilities capabilities = ProductionBatteryPort.CreateCapabilities(
            catalog,
            ProductionBatteryReadEvidence.Production);
        Assert.True(capabilities.PerformancePresets.SetEquals(
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
                PerformancePreset.Custom,
                PerformancePreset.BatterySaver,
                PerformancePreset.BatteryBalanced,
            ]));
        Assert.True(capabilities.FanModes.SetEquals(
            [FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve, FanMode.Maximum]));
        Assert.Empty(capabilities.LightingEffects);
        Assert.True(capabilities.OneShotLaptopControls.SetEquals(
        [
            OneShotLaptopControl.StartupAnimation,
            OneShotLaptopControl.FunctionKeyPrimary,
            OneShotLaptopControl.GamingMode,
        ]));

        RazerCommandDefinition catalogCommand(string action)
        {
            Assert.True(catalog.TryGet(action, out RazerCommandDefinition? command));
            return command!;
        }
    }

    [Fact]
    public void ProductionReadEvidenceIsCodeControlledAndAcceptsNoRuntimeInput()
    {
        Assert.True(ProductionBatteryReadEvidence.Production.SupportsTemporaryFullChargeQuery);
        Assert.True(ProductionBatteryReadEvidence.Production.SupportsAdapterPowerQuery);
        Assert.True(ProductionBatteryReadEvidence.Production.SupportsMcuVersionQuery);
        Assert.True(ProductionBatteryReadEvidence.Production.SupportsStartupAnimationQuery);
        Assert.Empty(typeof(ProductionBatteryReadEvidence).GetConstructors());
        Assert.DoesNotContain(
            typeof(ProductionBatteryReadEvidence).GetMethods(),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string)
                || parameter.ParameterType == typeof(byte[])
                || parameter.ParameterType == typeof(ReadOnlyMemory<byte>)));
    }
}
