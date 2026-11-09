namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Theory]
    [InlineData((byte)70, null)]
    [InlineData(null, LogoLightingMode.Breathing)]
    public async Task IndependentLightingRequest_RoundTripsOnlyTypedOptionalFields(byte? brightness, LogoLightingMode? logo)
    {
        var request = new SetIndependentLightingOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
            new(new(brightness, logo), OverrideLifetime.Persistent));
        Assert.Equal(request, Assert.IsType<SetIndependentLightingOverrideRequest>(await RoundTripRequestAsync(request)));
    }

    [Fact]
    public async Task IndependentLightingRequest_ClearRoundTripsAndMalformedFieldsAreRejected()
    {
        var clear = new SetIndependentLightingOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), null);
        Assert.Null(Assert.IsType<SetIndependentLightingOverrideRequest>(await RoundTripRequestAsync(clear)).Value);
        foreach (IndependentLightingSettings fields in new IndependentLightingSettings[] { new(), new(101), new(null, (LogoLightingMode)99) })
        {
            using MemoryStream stream = new();
            await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.WriteRequestAsync(stream,
                clear with { Value = new(fields, OverrideLifetime.Persistent) }, default));
        }
    }

    [Fact]
    public async Task IndependentLightingStatus_RetainsUnknownEffectAndObservedOptionalFields()
    {
        ServiceStatusSnapshot status = CreateIndependentStatus();
        ServiceStatusSnapshot result = await RoundTripStatusAsync(status);
        Assert.True(result.Capabilities!.SupportsIndependentLightingControls);
        Assert.Null(result.ConfirmedState!.Lighting);
        Assert.Equal(new IndependentLightingSettings(70, null), result.ConfirmedState.IndependentLighting);
        Assert.Equal(status.LightingOverride, result.LightingOverride);
    }

    [Fact]
    public async Task Protocol51_IndependentLightingIsRemovedFromEveryStatusLocation()
    {
        ServiceStatusSnapshot status = CreateIndependentStatus();
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            Protocol51Frame(status, PerformancePreset.Balanced, response: true), default);
        Assert.True(ReadFrozenProtocol51Frame(stream).Status!.WritesBlocked);
        string json = FramedJsonObject(stream).ToJsonString();
        Assert.DoesNotContain("independentControls", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("independentLighting", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supportsIndependentLightingControls", json, StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceStatusSnapshot CreateIndependentStatus()
    {
        ServiceStatusSnapshot status = CreateStatus();
        LightingSettings wrapper = status.Profiles.PluggedIn.Lighting with { IndependentControls = new(70, null) };
        return status with
        {
            Capabilities = status.Capabilities! with { SupportsIndependentLightingControls = true },
            ConfirmedState = new(null, null, null, null) { IndependentLighting = new(70, null) },
            Profiles = status.Profiles with { PluggedIn = status.Profiles.PluggedIn with { Lighting = wrapper } },
            DesiredState = status.DesiredState with { Lighting = status.DesiredState.Lighting with { Value = wrapper } },
            LightingOverride = new(wrapper, OverrideLifetime.Persistent),
        };
    }
}
