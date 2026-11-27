using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task AudioFixture_ReadsBothEndpointsAndReportsSyntheticUnavailability()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using (var host = Host.Start(id, mode: "audio-foreground"))
        {
            await host.WaitForReadyAsync(token);
            foreach (ThxAudioEndpoint endpoint in Enum.GetValues<ThxAudioEndpoint>())
            {
                SessionAudioResponse response = await ReadFixtureAudioAsync(id, endpoint, token);
                Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
                Assert.Equal(ThxAudioAvailability.AvailableReadOnly, response.Availability);
                Assert.Null(response.Error);
                Assert.Null(response.BaseAudio);
                ThxAudioState state = Assert.IsType<ThxAudioState>(response.State);
                Assert.Equal(endpoint, state.Endpoint);
                Assert.Equal(endpoint == ThxAudioEndpoint.Speakers, state.SpatialEnabled);
                Assert.Equal(ThxAudioFeatureAccess.Unavailable, state.Equalizer);
                Assert.Equal(ThxAudioFeatureAccess.Unavailable, state.ApplicationProfiles);
                Assert.Empty(state.Presets);
                Assert.Empty(state.EqualizerBands);
                Assert.Null(state.SystemEffectsDisabled);
            }
            await host.StopAsync(token);
        }

        await using var unavailable = Host.Start(id, "unavailable", mode: "audio-foreground");
        await unavailable.WaitForReadyAsync(token);
        SessionAudioResponse missing = await ReadFixtureAudioAsync(id, ThxAudioEndpoint.Speakers, token);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, missing.Status);
        Assert.Equal(ThxAudioAvailability.ComponentsUnavailable, missing.Availability);
        Assert.Equal("The synthetic Session Audio provider has no state.", missing.Error);
        Assert.Null(missing.State);
        Assert.Null(missing.BaseAudio);
        await unavailable.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task AudioFixture_RejectsUnsupportedOperationsAndMalformedFramesThenReadsAgain()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, mode: "audio-foreground");
        await host.WaitForReadyAsync(token);
        await using (var duplicate = Host.Start(id, mode: "audio-foreground"))
        {
            await duplicate.AssertRejectedAsync(token);
        }
        int version = SessionAudioIpc.ProtocolVersion;
        Guid requestId = Guid.NewGuid();
        const ThxAudioEndpoint endpoint = ThxAudioEndpoint.Speakers;
        SessionAudioRequest[] unsupported =
        [
            new DiscoverThxAudioRequest(version, requestId),
            new ConnectThxAudioRequest(version, requestId, endpoint),
            new SetThxSpatialRequest(version, requestId, endpoint, true),
            new SetThxEqualizerRequest(version, requestId, endpoint, true),
            new SelectThxPresetRequest(version, requestId, endpoint, "custom", new double[7]),
            new SetThxEnhancementRequest(version, requestId, endpoint, ThxAudioEnhancementKind.VoiceClarity, true, 50),
            new ApplyThxApplicationProfileRequest(version, requestId, endpoint, "fixture.exe", ThxAudioApplicationMode.Immersive),
        ];
        foreach (SessionAudioRequest request in unsupported)
        {
            using var pipe = AudioFixturePipe(id);
            await pipe.ConnectAsync(token);
            await SessionAudioIpcCodec.WriteRequestAsync(pipe, request, token);
            await AssertClosedAsync(pipe, token);
        }
        using (var anonymous = AudioFixturePipe(id, TokenImpersonationLevel.Anonymous))
        {
            await anonymous.ConnectAsync(token);
            await SessionAudioIpcCodec.WriteRequestAsync(anonymous,
                new ReadThxAudioRequest(version, requestId, endpoint), token);
            await AssertClosedAsync(anonymous, token);
        }
        foreach (string body in new[]
        {
            $$"""{"operation":"read","version":1,"requestId":"{{requestId}}","endpoint":0}""",
            $$"""{"operation":"read","version":{{version}},"requestId":"{{Guid.Empty}}","endpoint":0}""",
            $$"""{"operation":"read","version":{{version}},"requestId":"{{requestId}}","endpoint":2}""",
            $$"""{"operation":"read","version":{{version}},"requestId":"{{requestId}}","endpoint":"speakers"}""",
            $$"""{"operation":"read","version":{{version}},"requestId":"{{requestId}}"}""",
            $$"""{"operation":"read","version":{{version}},"requestId":"{{requestId}}","endpoint":0,"extra":true}""",
            $$"""{"operation":"provision","version":{{version}},"requestId":"{{requestId}}"}""",
        })
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            byte[] frame = new byte[sizeof(int) + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame, sizeof(int));
            using var pipe = AudioFixturePipe(id);
            await pipe.ConnectAsync(token);
            await pipe.WriteAsync(frame, token);
            await AssertClosedAsync(pipe, token);
        }
        using (var oversized = AudioFixturePipe(id))
        {
            await oversized.ConnectAsync(token);
            byte[] prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, SessionAudioIpc.MaximumMessageBytes + 1);
            await oversized.WriteAsync(prefix, token);
            await AssertClosedAsync(oversized, token);
        }
        using (var partial = AudioFixturePipe(id))
        {
            await partial.ConnectAsync(token);
            await partial.WriteAsync(new byte[] { 1 }, token);
            await AssertClosedAsync(partial, token);
        }
        Assert.Equal(ThxAudioOperationStatus.Succeeded, (await ReadFixtureAudioAsync(id, endpoint, token)).Status);
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task AudioFixture_DispatchDeadlineRetiresOnlyTheStalledConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "stalled", mode: "audio-foreground");
        await host.WaitForReadyAsync(token);
        var request = new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioEndpoint.Headphones);
        using (var pipe = AudioFixturePipe(id))
        {
            await pipe.ConnectAsync(token);
            await SessionAudioIpcCodec.WriteRequestAsync(pipe, request, token);
            var elapsed = Stopwatch.StartNew();
            await AssertClosedAsync(pipe, token);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(9), $"Dispatch closed after {elapsed.Elapsed}.");
        }
        Assert.Equal(ThxAudioOperationStatus.Succeeded,
            (await ReadFixtureAudioAsync(id, ThxAudioEndpoint.Speakers, token)).Status);

        using var stopped = AudioFixturePipe(id);
        await stopped.ConnectAsync(token);
        await SessionAudioIpcCodec.WriteRequestAsync(stopped, request, token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        caller.CancelAfter(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SessionAudioIpcCodec.ReadResponseAsync(stopped, caller.Token));

        using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        stopTimeout.CancelAfter(TimeSpan.FromSeconds(4));
        await host.StopAsync(stopTimeout.Token);
        await AssertClosedAsync(stopped, token);
    }

    [RustSessionAgentFact]
    public async Task AudioFixture_SpatialRequiresAnOutputReadAndReturnsConfirmedState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        const int version = SessionAudioIpc.ProtocolVersion;
        foreach (string scenario in new[] { "modern", "legacy" })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario, mode: "audio-foreground");
            await host.WaitForReadyAsync(token);

            var speakerWrite = new SetThxSpatialRequest(version, Guid.NewGuid(), ThxAudioEndpoint.Speakers, false);
            SessionAudioResponse beforeRead = await SendFixtureAudioAsync(id, speakerWrite, token);
            Assert.Equal(ThxAudioOperationStatus.Unavailable, beforeRead.Status);
            Assert.Null(beforeRead.State);
            SessionAudioResponse baseline = await ReadFixtureAudioAsync(id, ThxAudioEndpoint.Speakers, token);
            Assert.Equal(ThxAudioOperationStatus.Succeeded, baseline.Status);
            Assert.True(Assert.IsType<ThxAudioState>(baseline.State).SpatialEnabled);

            var headphoneWrite = new SetThxSpatialRequest(version, Guid.NewGuid(), ThxAudioEndpoint.Headphones, true);
            Assert.Equal(ThxAudioOperationStatus.Unavailable,
                (await SendFixtureAudioAsync(id, headphoneWrite, token)).Status);
            SessionAudioResponse applied = await SendFixtureAudioAsync(
                id, speakerWrite with { RequestId = Guid.NewGuid() }, token);
            Assert.Equal(ThxAudioOperationStatus.Succeeded, applied.Status);
            Assert.Equal(ThxAudioAvailability.Available, applied.Availability);
            Assert.Null(applied.BaseAudio);
            ThxAudioState state = Assert.IsType<ThxAudioState>(applied.State);
            Assert.Equal(ThxAudioEndpoint.Speakers, state.Endpoint);
            Assert.False(state.SpatialEnabled);
            Assert.Equal(scenario == "legacy" ? ThxAudioFeatureAccess.ObservedOnly : ThxAudioFeatureAccess.Unavailable,
                state.Equalizer);
            Assert.Equal(ThxAudioFeatureAccess.Unavailable, state.ApplicationProfiles);
            Assert.False(Assert.IsType<ThxAudioState>(
                (await ReadFixtureAudioAsync(id, ThxAudioEndpoint.Speakers, token)).State).SpatialEnabled);
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task AudioFixture_AmbiguousSpatialBlocksAcrossConnectionsUntilExplicitRead()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        const int version = SessionAudioIpc.ProtocolVersion;
        const ThxAudioEndpoint endpoint = ThxAudioEndpoint.Speakers;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "ambiguous", mode: "audio-foreground");
        await host.WaitForReadyAsync(token);
        Assert.True(Assert.IsType<ThxAudioState>((await ReadFixtureAudioAsync(id, endpoint, token)).State).SpatialEnabled);
        Assert.Equal(ThxAudioOperationStatus.Succeeded,
            (await ReadFixtureAudioAsync(id, ThxAudioEndpoint.Headphones, token)).Status);

        SessionAudioResponse unknown = await SendFixtureAudioAsync(id,
            new SetThxSpatialRequest(version, Guid.NewGuid(), endpoint, false), token);
        Assert.Equal(ThxAudioOperationStatus.Indeterminate, unknown.Status);
        Assert.Null(unknown.State);

        SessionAudioRequest[] blocked =
        [
            new ConnectThxAudioRequest(version, Guid.NewGuid(), endpoint),
            new SetThxSpatialRequest(version, Guid.NewGuid(), endpoint, true),
            new SetThxSpatialRequest(version, Guid.NewGuid(), ThxAudioEndpoint.Headphones, true),
        ];
        foreach (SessionAudioRequest request in blocked)
        {
            SessionAudioResponse refusal = await SendFixtureAudioAsync(id, request, token);
            Assert.Equal(ThxAudioOperationStatus.Unavailable, refusal.Status);
            Assert.Null(refusal.State);
        }

        SessionAudioResponse recovered = await ReadFixtureAudioAsync(id, endpoint, token);
        Assert.False(Assert.IsType<ThxAudioState>(recovered.State).SpatialEnabled);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, (await SendFixtureAudioAsync(id,
            new SetThxSpatialRequest(version, Guid.NewGuid(), ThxAudioEndpoint.Headphones, true), token)).Status);
        SessionAudioResponse next = await SendFixtureAudioAsync(id,
            new SetThxSpatialRequest(version, Guid.NewGuid(), endpoint, true), token);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, next.Status);
        Assert.True(Assert.IsType<ThxAudioState>(next.State).SpatialEnabled);
        await host.StopAsync(token);
    }

    private static Task<SessionAudioResponse> ReadFixtureAudioAsync(
        string id, ThxAudioEndpoint endpoint, CancellationToken token) =>
        SendFixtureAudioAsync(id, new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), endpoint), token);

    private static async Task<SessionAudioResponse> SendFixtureAudioAsync(
        string id, SessionAudioRequest request, CancellationToken token)
    {
        using var pipe = AudioFixturePipe(id);
        await pipe.ConnectAsync(token);
        await SessionAudioIpcCodec.WriteRequestAsync(pipe, request, token);
        SessionAudioResponse response = await SessionAudioIpcCodec.ReadResponseAsync(pipe, token);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(request.Version, response.Version);
        return response;
    }


    private static NamedPipeClientStream AudioFixturePipe(
        string id, TokenImpersonationLevel impersonation = TokenImpersonationLevel.Identification) =>
        new(".", $"OpenBlade.Rust.Session.Audio.Dev.{id}", PipeDirection.InOut, PipeOptions.Asynchronous, impersonation);
}
