using System.Buffers.Binary;
using System.Text;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class SessionAudioIpcCodecTests
{
    [Fact]
    public async Task BaseAudioProofRoundTripsAndAbsentFieldPreservesResponseShape()
    {
        var response = new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
            ThxAudioOperationStatus.Unavailable, ThxAudioAvailability.ComponentsUnavailable, "THX is absent.", null);
        using var absent = new MemoryStream();
        await SessionAudioIpcCodec.WriteResponseAsync(absent, response);
        Assert.DoesNotContain("baseAudio", Encoding.UTF8.GetString(absent.ToArray()), StringComparison.Ordinal);
        absent.Position = 0;
        Assert.Null((await SessionAudioIpcCodec.ReadResponseAsync(absent)).BaseAudio);

        var proof = new ThxBaseAudioDiagnosticProof(true, false, true,
            new string('x', SessionAudioIpc.MaximumBaseAudioDetailCharacters));
        using var present = new MemoryStream();
        await SessionAudioIpcCodec.WriteResponseAsync(present, response with { BaseAudio = proof });
        present.Position = 0;
        Assert.Equal(proof, (await SessionAudioIpcCodec.ReadResponseAsync(present)).BaseAudio);
    }

    [Theory]
    [InlineData(false, true, false, "Ready without root")]
    [InlineData(false, false, true, "Install without root")]
    [InlineData(true, true, true, "Contradictory state")]
    [InlineData(false, false, false, "")]
    [InlineData(false, false, false, "line\nbreak")]
    public async Task BaseAudioProofRejectsInvalidFlagsAndText(bool root, bool ready, bool install, string detail)
    {
        var response = new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
            ThxAudioOperationStatus.Unavailable, ThxAudioAvailability.ComponentsUnavailable, "Unavailable.", null)
        { BaseAudio = new(root, ready, install, detail) };
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionAudioIpcCodec.WriteResponseAsync(stream, response));
    }

    [Fact]
    public async Task BaseAudioProofRejectsOversizedIncomingTextAndNativeState()
    {
        var response = new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded, ThxAudioAvailability.Available, null, null)
        { BaseAudio = new(true, true, false, new string('x', SessionAudioIpc.MaximumBaseAudioDetailCharacters + 1)) };
        byte[] payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var incoming = new MemoryStream();
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        incoming.Write(prefix);
        incoming.Write(payload);
        incoming.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionAudioIpcCodec.ReadResponseAsync(incoming));
        using var outgoing = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionAudioIpcCodec.WriteResponseAsync(outgoing,
            response with { State = State(ThxAudioEndpoint.Speakers), BaseAudio = new(true, true, false, "Ready.") }));
    }

    [Fact]
    public async Task ResponsePreservesLegacyEqAccessWithoutInventingAnEnabledState()
    {
        var response = new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded, ThxAudioAvailability.Available, null,
            new ThxAudioState(ThxAudioEndpoint.Speakers, true, false, null, [])
            { Equalizer = ThxAudioFeatureAccess.ObservedOnly, ApplicationProfiles = ThxAudioFeatureAccess.Unavailable });
        using var stream = new MemoryStream();
        await SessionAudioIpcCodec.WriteResponseAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        var decoded = await SessionAudioIpcCodec.ReadResponseAsync(stream, CancellationToken.None);
        Assert.Equal(ThxAudioFeatureAccess.ObservedOnly, decoded.State!.Equalizer);
        Assert.True(decoded.State.SpatialEnabled);
        Assert.Empty(decoded.State.Presets);
    }

    [Fact]
    public async Task Response_RoundTripsRznb11TenBandSpeakers()
    {
        ThxAudioState baseline = State(ThxAudioEndpoint.Speakers);
        int[] frequencies = [31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        ThxAudioState state = baseline with
        {
            EqualizerBands = frequencies.Select(frequency => new ThxAudioEqualizerBand(frequency, 0)).ToArray(),
            FactoryPresetCurves = baseline.FactoryPresetCurves.Select(curve =>
                curve with { GainsDb = new[] { 0d, 0d, 0d }.Concat(curve.GainsDb).ToArray() }).ToArray(),
        };
        var response = new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available, null, state);
        using var stream = new MemoryStream();
        await SessionAudioIpcCodec.WriteResponseAsync(stream, response);
        stream.Position = 0;
        SessionAudioResponse decoded = await SessionAudioIpcCodec.ReadResponseAsync(stream);
        Assert.Equal(frequencies, decoded.State!.EqualizerBands.Select(band => band.FrequencyHz));
        Assert.All(decoded.State.FactoryPresetCurves, curve => Assert.Equal(10, curve.GainsDb.Count));
    }

    [Theory]
    [InlineData(ThxAudioAvailability.Available)]
    [InlineData(ThxAudioAvailability.AvailableReadOnly)]
    public async Task Response_RoundTripsSourceBackedAudioState(ThxAudioAvailability availability)
    {
        ThxAudioState state = State(ThxAudioEndpoint.Speakers);
        var response = new SessionAudioResponse(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded,
            availability,
            null,
            state);
        using var stream = new MemoryStream();

        await SessionAudioIpcCodec.WriteResponseAsync(stream, response);
        stream.Position = 0;
        SessionAudioResponse decoded = await SessionAudioIpcCodec.ReadResponseAsync(stream);

        Assert.Equal(availability, decoded.Availability);
        Assert.Equal(state.Endpoint, decoded.State!.Endpoint);
        Assert.Equal(state.SelectedNativeProfileId, decoded.State.SelectedNativeProfileId);
        Assert.Equal(state.Presets, decoded.State!.Presets);
        Assert.Equal(state.EqualizerBands, decoded.State.EqualizerBands);
        Assert.Equal(
            state.FactoryPresetCurves.Select(curve => curve.PresetId),
            decoded.State.FactoryPresetCurves.Select(curve => curve.PresetId));
        Assert.All(state.FactoryPresetCurves.Select((curve, index) => (curve, index)), value =>
            Assert.Equal(
                value.curve.GainsDb,
                decoded.State.FactoryPresetCurves[value.index].GainsDb));
        Assert.Equal(state.SoundNormalization, decoded.State.SoundNormalization);
        Assert.Equal(state.VoiceClarity, decoded.State.VoiceClarity);
        Assert.Equal(state.ApplicationProfileCatalog, decoded.State.ApplicationProfileCatalog);
        Assert.Equal(
            state.ApplicationAutomationPaused,
            decoded.State.ApplicationAutomationPaused);
    }

    [Fact]
    public async Task Response_RejectsUnknownPresetAndInvalidBands()
    {
        var response = new SessionAudioResponse(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available,
            null,
            State(ThxAudioEndpoint.Speakers) with
            {
                Presets = [new ThxAudioPreset("ApexLegends_imm", "Apex Legends")],
                EqualizerBands = [new ThxAudioEqualizerBand(250, 13)],
            });
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionAudioIpcCodec.WriteResponseAsync(stream, response));
    }

    public static TheoryData<SessionAudioRequest> Requests => new()
    {
        new DiscoverThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid()),
        new ConnectThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioEndpoint.Speakers),
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
            "music",
            [-3, -2, -1, 0, 1, 2, 3]),
        new SetThxEnhancementRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Speakers,
            ThxAudioEnhancementKind.SoundNormalization,
            true,
            55),
        new SetThxEnhancementRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Headphones,
            ThxAudioEnhancementKind.VoiceClarity,
            true,
            LevelPercent: null),
        new SetThxEnhancementRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Headphones,
            ThxAudioEnhancementKind.VoiceClarity,
            false,
            LevelPercent: null),
        new ApplyThxApplicationProfileRequest(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioEndpoint.Headphones,
            "r5apex.exe",
            ThxAudioApplicationMode.Competitive),
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Request_RoundTripsEveryTypedOperation(SessionAudioRequest request)
    {
        using var stream = new MemoryStream();

        await SessionAudioIpcCodec.WriteRequestAsync(stream, request);
        stream.Position = 0;
        SessionAudioRequest decoded = await SessionAudioIpcCodec.ReadRequestAsync(stream);

        Assert.Equal(request.GetType(), decoded.GetType());
        Assert.Equal(request.Version, decoded.Version);
        Assert.Equal(request.RequestId, decoded.RequestId);
        if (request is SelectThxPresetRequest expectedPreset)
        {
            SelectThxPresetRequest actualPreset = Assert.IsType<SelectThxPresetRequest>(decoded);
            Assert.Equal(expectedPreset.Endpoint, actualPreset.Endpoint);
            Assert.Equal(expectedPreset.PresetId, actualPreset.PresetId);
            Assert.Equal(expectedPreset.GainsDb, actualPreset.GainsDb);
        }
        else
        {
            Assert.Equal(request, decoded);
        }
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
            " music ",
            [0, 0, 0, 0, 0, 0, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionAudioIpcCodec.WriteRequestAsync(malformed, request));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionAudioIpcCodec.WriteRequestAsync(
                new MemoryStream(),
                new SelectThxPresetRequest(
                    SessionAudioIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    ThxAudioEndpoint.Speakers,
                    "custom",
                    [0, 0, 0])));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionAudioIpcCodec.WriteRequestAsync(
                new MemoryStream(),
                new SelectThxPresetRequest(
                    SessionAudioIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    ThxAudioEndpoint.Headphones,
                    "custom",
                    [0, 0, 0, 0, 0, 0, 0, 0, 0, 13])));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionAudioIpcCodec.WriteRequestAsync(
                new MemoryStream(),
                new SetThxEnhancementRequest(
                    SessionAudioIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    ThxAudioEndpoint.Headphones,
                    ThxAudioEnhancementKind.SoundNormalization,
                    true,
                    50)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionAudioIpcCodec.WriteRequestAsync(
                new MemoryStream(),
                new SetThxEnhancementRequest(
                    SessionAudioIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    ThxAudioEndpoint.Headphones,
                    ThxAudioEnhancementKind.VoiceClarity,
                    false,
                    50)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionAudioIpcCodec.WriteRequestAsync(
                new MemoryStream(),
                new ApplyThxApplicationProfileRequest(
                    SessionAudioIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    ThxAudioEndpoint.Headphones,
                    "..\\r5apex.exe",
                    ThxAudioApplicationMode.Immersive)));
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

    private static ThxAudioState State(ThxAudioEndpoint endpoint)
    {
        int[] frequencies = endpoint == ThxAudioEndpoint.Speakers
            ? [250, 500, 1000, 2000, 4000, 8000, 16000]
            : [31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        return new ThxAudioState(
            endpoint,
            SpatialEnabled: true,
            EqualizerEnabled: true,
            SelectedPresetId: "music",
            Presets:
            [
                new ThxAudioPreset("game", "Game"),
                new ThxAudioPreset("movie", "Movie"),
                new ThxAudioPreset("music", "Music"),
                new ThxAudioPreset("voice", "Voice"),
                new ThxAudioPreset("custom", "Custom"),
                new ThxAudioPreset("default", "Default"),
            ])
        {
            SelectedNativeProfileId = "music",
            EqualizerBands = frequencies
                .Select((frequency, index) => new ThxAudioEqualizerBand(frequency, index - 3))
                .ToArray(),
            FactoryPresetCurves = new[] { "game", "movie", "music", "voice", "custom" }
                .Select(id => new ThxAudioPresetCurve(
                    id,
                    Enumerable.Repeat(0d, frequencies.Length).ToArray()))
                .ToArray(),
            SoundNormalization = endpoint == ThxAudioEndpoint.Speakers
                ? new ThxAudioEnhancement(false, 55)
                : null,
            VoiceClarity = new ThxAudioEnhancement(true, 60),
            ApplicationProfiles = ThxAudioFeatureAccess.Available,
            ApplicationProfileCatalog =
            [
                new ThxAudioApplicationProfile(
                    "r5apex.exe",
                    SupportsImmersive: true,
                    SupportsCompetitive: true),
            ],
            ApplicationAutomationPaused = true,
        };
    }
}
