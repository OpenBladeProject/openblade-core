using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Fact]
    public async Task UnclassifiedExternalPresenceAndOrdinaryCapability_RoundTripWithoutManufacturedClassification()
    {
        ServiceStatusSnapshot expected = CreateStatus() with
        {
            ExternalPowerPresent = true,
            ActivePowerSource = PowerSource.ExternalUnknown,
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                SupportsOrdinaryThermalControlOnUnclassifiedExternalPower = true,
            },
        };
        ServiceStatusSnapshot actual = await RoundTripStatusAsync(expected);
        Assert.True(actual.ExternalPowerPresent);
        Assert.True(actual.Capabilities!.SupportsOrdinaryThermalControlOnUnclassifiedExternalPower);
        Assert.Equal(PowerSource.ExternalUnknown, actual.ActivePowerSource);
        Assert.False(CreateStatus().ExternalPowerPresent);
        Assert.False(DeviceCapabilities.Unavailable().SupportsOrdinaryThermalControlOnUnclassifiedExternalPower);
    }

    [Fact]
    public async Task Protocol51Projection_OmitsExternalPresenceAndOrdinaryAdmissionCapability()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            ExternalPowerPresent = true,
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                SupportsOrdinaryThermalControlOnUnclassifiedExternalPower = true,
            },
        };
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        JsonObject root = FramedJsonObject(stream);
        JsonObject projected = Assert.IsType<JsonObject>(root["status"]);
        Assert.False(projected.ContainsKey("externalPowerPresent"));
        Assert.False(Assert.IsType<JsonObject>(projected["capabilities"])
            .ContainsKey("supportsOrdinaryThermalControlOnUnclassifiedExternalPower"));
    }
}
