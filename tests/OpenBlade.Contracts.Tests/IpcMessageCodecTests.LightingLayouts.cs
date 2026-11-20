using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Fact]
    public async Task CurrentProtocol_RoundTripsReactiveLayoutWithoutSequenceCapability()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = Enum.GetValues<LightingEffect>()
                .Where(effect => effect != LightingEffect.Sequence).ToHashSet(),
            SupportsUniformLightingFrames = true,
            SupportsIndependentLightingControls = true,
            SupportsVaporChamberLighting = true,
            SupportsTouchpadStatusIndicator = true,
            KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0529,
            ObservedLightingZones = new HashSet<ObservedLightingZone>
            {
                ObservedLightingZone.Touchpad,
                ObservedLightingZone.UnderGlass,
            },
        };
        ServiceStatusSnapshot status = CreateStatus() with { Capabilities = capabilities };

        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(status);

        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0529, decoded.Capabilities!.KeyboardLightingLayoutId);
        Assert.DoesNotContain(LightingEffect.Sequence, decoded.Capabilities.LightingEffects);
        Assert.True(decoded.Capabilities.SupportsUniformLightingFrames);
        Assert.True(decoded.Capabilities.SupportsVaporChamberLighting);
        Assert.True(decoded.Capabilities.SupportsTouchpadStatusIndicator);
        Assert.True(decoded.Capabilities.ObservedLightingZones.SetEquals(capabilities.ObservedLightingZones));
    }

    [Fact]
    public async Task CurrentProtocol_RoundTripsBlade14LayoutAndTypedSequence()
    {
        const string layoutId = KeyboardLightingLayoutIds.Rz09_0530;
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];
        int firstKeySlot = Enumerable.Range(0, AmbientLightingFrame.SlotCount)
            .First(Blade14KeyboardLightingLayout.IsKeySlot);
        slots[firstKeySlot] = new RgbColor(1, 2, 3);
        string timeline = KeyboardLightingSequenceCodec.Encode(
            [new KeyboardLightingSequenceFrame(50, slots)],
            layoutId);
        LightingSettings lighting = LightingSettingsFactory.Sequence(
            layoutId, timeline, loop: true, brightness: 100);
        var request = new SetLightingOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<LightingSettings>(lighting, OverrideLifetime.Persistent));
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        SetLightingOverrideRequest decodedRequest = Assert.IsType<SetLightingOverrideRequest>(
            await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
        Assert.Equal(lighting, decodedRequest.Value!.Value);

        ServiceStatusSnapshot baseline = CreateStatus();
        ServiceStatusSnapshot status = baseline with
        {
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                LightingEffects = new HashSet<LightingEffect> { LightingEffect.Sequence },
                KeyboardLightingLayoutId = layoutId,
            },
            ConfirmedState = new ConfirmedDeviceState(null, null, null, lighting),
            LightingOverride = request.Value,
        };
        ServiceStatusSnapshot decodedStatus = await RoundTripStatusAsync(status);

        Assert.Equal(layoutId, decodedStatus.Capabilities!.KeyboardLightingLayoutId);
        Assert.Contains(LightingEffect.Sequence, decodedStatus.Capabilities.LightingEffects);
        Assert.Equal(lighting, decodedStatus.ConfirmedState!.Lighting);
        Assert.Equal(request.Value, decodedStatus.LightingOverride);
    }

    [Fact]
    public async Task CurrentProtocol_RoundTripsRz09529SequenceCapability()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect> { LightingEffect.Sequence },
            KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0529,
        };
        ServiceStatusSnapshot status = CreateStatus() with { Capabilities = capabilities };
        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(status);

        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0529, decoded.Capabilities!.KeyboardLightingLayoutId);
        Assert.Contains(LightingEffect.Sequence, decoded.Capabilities.LightingEffects);
    }

    [Fact]
    public async Task CurrentProtocol_RejectsSequenceCapabilityWithoutKeyboardLayout()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect> { LightingEffect.Sequence },
        };
        ServiceStatusSnapshot status = CreateStatus() with { Capabilities = capabilities };
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.WriteServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(255)]
    public async Task CurrentProtocol_RoundTripsBlade14KeyboardEdition(byte edition)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0530,
            Blade14KeyboardEdition = edition,
        };
        ServiceStatusSnapshot status = CreateStatus() with { Capabilities = capabilities };
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        JsonObject json = FramedJsonObject(stream);
        Assert.Equal(edition, json["status"]!["capabilities"]!["blade14KeyboardEdition"]!.GetValue<byte>());

        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(status);

        Assert.Equal(edition, decoded.Capabilities!.Blade14KeyboardEdition);
    }

    [Fact]
    public async Task CurrentProtocol_OmittedBlade14KeyboardEditionDecodesAsCommonMapFallback()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0530,
            Blade14KeyboardEdition = 12,
        };
        using MemoryStream encoded = new();
        await IpcMessageCodec.WriteServerFrameAsync(encoded,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateStatus() with { Capabilities = capabilities }),
            CancellationToken.None);
        JsonObject json = FramedJsonObject(encoded);
        Assert.Equal(OpenBladeIpc.ProtocolVersion, json["version"]!.GetValue<int>());
        JsonObject projected = Assert.IsType<JsonObject>(json["status"]!["capabilities"]);
        // Remove the member itself, as a sender without the field would.
        Assert.True(projected.Remove("blade14KeyboardEdition"));

        using MemoryStream frame = FrameJson(json.ToJsonString());
        ServiceStatusEvent decoded = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(frame, CancellationToken.None));

        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0530, decoded.Status.Capabilities!.KeyboardLightingLayoutId);
        Assert.Null(decoded.Status.Capabilities.Blade14KeyboardEdition);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(KeyboardLightingLayoutIds.Rz09_0484)]
    [InlineData(KeyboardLightingLayoutIds.Rz09_0529)]
    public async Task CurrentProtocol_RejectsBlade14KeyboardEditionWithoutItsLayout(string? layoutId)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            KeyboardLightingLayoutId = layoutId,
            Blade14KeyboardEdition = 12,
        };
        ServiceStatusSnapshot status = CreateStatus() with { Capabilities = capabilities };
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.WriteServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None));
    }
}
