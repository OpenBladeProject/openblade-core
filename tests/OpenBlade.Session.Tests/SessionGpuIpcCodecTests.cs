using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class SessionGpuIpcCodecTests
{
    [Fact]
    public async Task CloseRequest_RoundTripsExactProcessIdentityAndForceIntent()
    {
        var expected = new CloseGpuAppsRequest(
            SessionGpuIpc.ProtocolVersion,
            Guid.NewGuid(),
            Force: true,
            [new SessionGpuAppIdentity(42, 123456, "game")]);
        using var stream = new MemoryStream();

        await SessionGpuIpcCodec.WriteRequestAsync(stream, expected);
        stream.Position = 0;
        SessionGpuRequest actual = await SessionGpuIpcCodec.ReadRequestAsync(stream);

        CloseGpuAppsRequest close = Assert.IsType<CloseGpuAppsRequest>(actual);
        Assert.Equal(expected.Version, close.Version);
        Assert.Equal(expected.RequestId, close.RequestId);
        Assert.Equal(expected.Force, close.Force);
        Assert.Equal(expected.Targets, close.Targets);
    }

    [Fact]
    public async Task CloseRequest_RejectsDuplicateProcessIds()
    {
        var request = new CloseGpuAppsRequest(
            SessionGpuIpc.ProtocolVersion,
            Guid.NewGuid(),
            Force: false,
            [
                new SessionGpuAppIdentity(42, 123456, "game"),
                new SessionGpuAppIdentity(42, 789012, "replacement"),
            ]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionGpuIpcCodec.WriteRequestAsync(new MemoryStream(), request));
    }

    [Fact]
    public async Task ForceCloseRequest_RejectsMultipleTargets()
    {
        var request = new CloseGpuAppsRequest(
            SessionGpuIpc.ProtocolVersion,
            Guid.NewGuid(),
            Force: true,
            [
                new SessionGpuAppIdentity(42, 123456, "game"),
                new SessionGpuAppIdentity(43, 789012, "overlay"),
            ]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionGpuIpcCodec.WriteRequestAsync(new MemoryStream(), request));
    }

    [Fact]
    public async Task VersionOneRequest_IsRejected()
    {
        var request = new QueryGpuAppsRequest(1, Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionGpuIpcCodec.WriteRequestAsync(new MemoryStream(), request));
    }

    [Fact]
    public async Task Response_RoundTripsEligibleBlockedAndPartialResults()
    {
        var expected = new SessionGpuResponse(
            SessionGpuIpc.ProtocolVersion,
            Guid.NewGuid(),
            SessionGpuOperationStatus.PartiallySucceeded,
            "Some applications could not be closed.",
            [
                new SessionGpuApp(42, 123456, "game", @"C:\Games\game.exe", 0, true, null),
                new SessionGpuApp(8, 987654, "dwm", null, 0.2, false, "Windows process"),
            ],
            [
                new SessionGpuActionResult(42, 123456, true, null),
                new SessionGpuActionResult(8, 987654, false, "Windows process"),
            ]);
        using var stream = new MemoryStream();

        await SessionGpuIpcCodec.WriteResponseAsync(stream, expected);
        stream.Position = 0;
        SessionGpuResponse actual = await SessionGpuIpcCodec.ReadResponseAsync(stream);

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Error, actual.Error);
        Assert.Equal(expected.Apps, actual.Apps);
        Assert.Equal(expected.Results, actual.Results);
    }

    [Fact]
    public async Task Response_RejectsCloseableAppWithBlockReason()
    {
        var response = new SessionGpuResponse(
            SessionGpuIpc.ProtocolVersion,
            Guid.NewGuid(),
            SessionGpuOperationStatus.Succeeded,
            null,
            [new SessionGpuApp(42, 123456, "game", null, 1, true, "Contradiction")],
            []);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionGpuIpcCodec.WriteResponseAsync(new MemoryStream(), response));
    }
}
