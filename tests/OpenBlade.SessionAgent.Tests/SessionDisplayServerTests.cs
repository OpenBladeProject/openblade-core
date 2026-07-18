using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;
using OpenBlade.SessionAgent;
using System.Security.Principal;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionDisplayServerTests
{
    [Fact]
    public async Task Dispatcher_MapsEveryTypedRequestAndPreservesCorrelationId()
    {
        var applier = new RecordingApplier();
        var dispatcher = new SessionDisplayRequestDispatcher(applier);
        SessionDisplayRequest[] requests =
        [
            new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, Guid.NewGuid()),
            new SetRefreshRateRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                "fixed-240"),
            new SetHdrRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                true),
            new SetColorProfileRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                "native.icm"),
            new ApplyDisplayProfileRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                new DisplayProfileSettings("fixed-60", false, "native.icm")),
        ];

        foreach (SessionDisplayRequest request in requests)
        {
            SessionDisplayResponse response = await dispatcher.DispatchAsync(
                request,
                CancellationToken.None);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(SessionDisplayOperationStatus.Succeeded, response.Status);
        }

        Assert.Equal(
            ["query", "refresh:PluggedIn:fixed-240", "hdr:PluggedIn:True",
             "color:PluggedIn:native.icm", "profile:PluggedIn:fixed-60"],
            applier.Calls);
    }

    [Fact]
    public async Task ServeConnection_UsesStrictCodecAndWritesTypedResponse()
    {
        var applier = new RecordingApplier();
        var dispatcher = new SessionDisplayRequestDispatcher(applier);
        await using var owner = AcquireOwner();
        await using var server = new SessionDisplayPipeServer(owner, dispatcher);
        var request = new QueryDisplayRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid());
        await using var requestBytes = new MemoryStream();
        await SessionDisplayIpcCodec.WriteRequestAsync(requestBytes, request);
        await using var connection = new TestDuplexStream(requestBytes.ToArray());

        await server.ServeConnectionAsync(connection, CancellationToken.None);
        connection.RewindOutput();
        SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(connection);

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(SessionDisplayOperationStatus.Succeeded, response.Status);
        Assert.Equal("fixed-60", response.State.CurrentRefreshKey);
    }

    private static SessionAgentPipeOwner AcquireOwner()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? owner));
        return owner!;
    }

    private sealed class RecordingApplier : IDisplayProfileApplier
    {
        public List<string> Calls { get; } = [];

        public Task<SessionDisplayExecutionResult> QueryAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("query");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"refresh:{expectedPowerSource}:{refreshRateKey}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> SetHdrAsync(
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"hdr:{expectedPowerSource}:{enabled}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"color:{expectedPowerSource}:{colorProfileKey}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"profile:{expectedPowerSource}:{settings.RefreshRateKey}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
            PowerSource expectedPowerSource,
            long expectedGeneration,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken) => Result();

        private static Task<SessionDisplayExecutionResult> Result() => Task.FromResult(
            new SessionDisplayExecutionResult(
                SessionDisplayOperationStatus.Succeeded,
                null,
                new DisplaySettingsSnapshot(
                    true,
                    "Internal display",
                    2560,
                    1600,
                    "fixed-60",
                    [new DisplayRefreshOption("fixed-60", "60 Hz", 60, 60, 60, false)],
                    true,
                    false,
                    true,
                    "native.icm",
                    [new DisplayColorProfile("native.icm", "Native", true)],
                    null)));
    }

    private sealed class TestDuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream reader = new(input, writable: false);
        private readonly MemoryStream writer = new();
        private bool readOutput;

        public void RewindOutput()
        {
            writer.Position = 0;
            readOutput = true;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            (readOutput ? writer : reader).Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            (readOutput ? writer : reader).ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            writer.WriteAsync(buffer, token);
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken token) => writer.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Dispose();
                writer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
