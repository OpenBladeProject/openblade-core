using System.Buffers.Binary;
using System.Text;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class SessionAudioIpcCodecTests
{
    [Theory]
    [InlineData(218)]
    [InlineData(256)]
    public async Task Response_RoundTripsObservedAndMaximumPresetCatalogs(int count)
    {
        var presets = Enumerable.Range(0, count)
            .Select(index => new ThxAudioPreset($"preset-{index}", $"preset-{index}"))
            .ToArray();
        var response = new SessionAudioResponse(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available,
            null,
            new ThxAudioState(ThxAudioEndpoint.Speakers, false, false, "preset-0", presets));
        using var stream = new MemoryStream();

        await SessionAudioIpcCodec.WriteResponseAsync(stream, response);
        stream.Position = 0;
        SessionAudioResponse decoded = await SessionAudioIpcCodec.ReadResponseAsync(stream);

        Assert.Equal(presets, decoded.State!.Presets);
    }

    [Fact]
    public async Task Response_RejectsPresetCatalogBeyondBound()
    {
        var presets = Enumerable.Range(0, 257)
            .Select(index => new ThxAudioPreset($"preset-{index}", $"preset-{index}"))
            .ToArray();
        var response = new SessionAudioResponse(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available,
            null,
            new ThxAudioState(ThxAudioEndpoint.Speakers, false, false, "preset-0", presets));
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionAudioIpcCodec.WriteResponseAsync(stream, response));
    }

    public static TheoryData<SessionAudioRequest> Requests => new()
    {
        new DiscoverThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid()),
        new ReadThxAudioRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Speakers),
        new SetThxSpatialRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Headphones,
            true),
        new SetThxEqualizerRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Speakers,
            false),
        new SelectThxPresetRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Speakers,
            "music"),
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Request_RoundTripsEveryTypedOperation(SessionAudioRequest request)
    {
        using var stream = new MemoryStream();

        await SessionAudioIpcCodec.WriteRequestAsync(stream, request);
        stream.Position = 0;
        SessionAudioRequest decoded = await SessionAudioIpcCodec.ReadRequestAsync(stream);

        Assert.Equal(request, decoded);
        Assert.Equal(request.GetType(), decoded.GetType());
    }

    [Fact]
    public async Task Response_RoundTripsConfirmedStateAndVendorPresetIds()
    {
        var response = new SessionAudioResponse(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available,
            null,
            new ThxAudioState(
                ThxAudioEndpoint.Speakers,
                SpatialEnabled: true,
                EqualizerEnabled: false,
                SelectedPresetId: "music",
                Presets:
                [
                    new ThxAudioPreset("music", "music"),
                    new ThxAudioPreset("game", "game"),
                ]));
        using var stream = new MemoryStream();

        await SessionAudioIpcCodec.WriteResponseAsync(stream, response);
        stream.Position = 0;
        SessionAudioResponse decoded = await SessionAudioIpcCodec.ReadResponseAsync(stream);

        Assert.Equal(response with { State = decoded.State }, decoded);
        Assert.Equal(response.State!.Presets, decoded.State!.Presets);
    }

    [Fact]
    public async Task Response_AllowsTypedControllerActiveDiscoveryFailure()
    {
        var response = new SessionAudioResponse(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioOperationStatus.Unavailable,
            ThxAudioAvailability.ControllerActive,
            "Close Synapse before connecting to THX audio.",
            null);
        using var stream = new MemoryStream();

        await SessionAudioIpcCodec.WriteResponseAsync(stream, response);
        stream.Position = 0;

        Assert.Equal(response, await SessionAudioIpcCodec.ReadResponseAsync(stream));
    }

    [Fact]
    public async Task Codec_RejectsUnreviewedPresetIdAndOversizedFrame()
    {
        using var malformed = new MemoryStream();
        var request = new SelectThxPresetRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Speakers,
            " music ");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionAudioIpcCodec.WriteRequestAsync(malformed, request));

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix,
            SessionAudioIpc.MaximumMessageBytes + 1);
        using var oversized = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionAudioIpcCodec.ReadRequestAsync(oversized));
    }

    [Fact]
    public async Task ReadRequest_RejectsUnknownMembers()
    {
        string json = $$"""
            {"operation":"discover","version":{{SessionAudioIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}","unexpected":true}
            """;
        using MemoryStream stream = Frame(json);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionAudioIpcCodec.ReadRequestAsync(stream));
    }

    private static MemoryStream Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return new MemoryStream(frame);
    }
}
