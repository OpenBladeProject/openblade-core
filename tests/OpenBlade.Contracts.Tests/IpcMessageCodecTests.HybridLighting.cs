using System.Text;
using System.Text.Json;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridLightingIntent_RoundTripsExplicitOptInAndDefaultsOmissionToFalse(bool enabled)
    {
        var request = new SetLightingOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
            new(LightingSettingsFactory.SpectrumCycling(70) with { ControlsKeyboardEffect = enabled }, OverrideLifetime.Persistent));
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(stream, request, default);
        string json = Encoding.UTF8.GetString(stream.ToArray().AsSpan(4));
        Assert.Equal(enabled, json.Contains("controlsKeyboardEffect", StringComparison.Ordinal));
        stream.Position = 0;
        var result = Assert.IsType<SetLightingOverrideRequest>(await IpcMessageCodec.ReadRequestAsync(stream, default));
        Assert.Equal(enabled, result.Value!.Value.ControlsKeyboardEffect);
        Assert.Equal(request, result);
    }

    [Fact]
    public async Task HybridLightingIntent_RejectsPreviousProtocolAndMalformedOptIn()
    {
        var request = new SetLightingOverrideRequest(80, Guid.NewGuid(),
            new(LightingSettingsFactory.SpectrumCycling(70) with { ControlsKeyboardEffect = true }, OverrideLifetime.Persistent));
        using var rejected = new MemoryStream();
        await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(() => IpcMessageCodec.WriteRequestAsync(rejected, request, default));
        Assert.Empty(rejected.ToArray());
        using var valid = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(valid, request with { Version = OpenBladeIpc.ProtocolVersion }, default);
        string json = Encoding.UTF8.GetString(valid.ToArray().AsSpan(4)).Replace(
            "\"controlsKeyboardEffect\":true", "\"controlsKeyboardEffect\":\"true\"", StringComparison.Ordinal);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        using var malformed = new MemoryStream();
        malformed.Write(BitConverter.GetBytes(payload.Length));
        malformed.Write(payload);
        malformed.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadRequestAsync(malformed, default));
    }
}
