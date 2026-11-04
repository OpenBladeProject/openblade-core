using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Platform;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionGpuServerTests
{
    [Fact]
    public async Task RunAsync_CancellationWhileWaitingCompletesNormally()
    {
        var dispatcher = new SessionGpuRequestDispatcher(new ThrowOnceGpuAppManager());
        await using SessionAgentPipeOwner owner = AcquireOwner();
        await using var server = new SessionGpuPipeServer(owner, dispatcher);
        using var cancellation = new CancellationTokenSource();

        Task running = server.RunAsync(cancellation.Token);
        cancellation.Cancel();

        await running.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunAsync_ClientClosesBeforeAccept_ReplacesListener()
    {
        var dispatcher = new SessionGpuRequestDispatcher(new ThrowOnceGpuAppManager());
        await using SessionAgentPipeOwner owner = AcquireOwner();
        await using var server = new SessionGpuPipeServer(owner, dispatcher);
        using var cancellation = new CancellationTokenSource();
        await ConnectAndCloseAsync(owner.Endpoint.PipeName);

        Task running = server.RunAsync(cancellation.Token);
        SessionGpuResponse first = await QueryAsync(owner.Endpoint.PipeName);
        SessionGpuResponse response = await QueryAsync(owner.Endpoint.PipeName);
        cancellation.Cancel();

        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionGpuOperationStatus.Failed, first.Status);
        Assert.Equal(SessionGpuOperationStatus.Succeeded, response.Status);
    }

    [Fact]
    public async Task DispatchFailureReturnsCorrelatedErrorAndKeepsServerUsable()
    {
        var manager = new ThrowOnceGpuAppManager();
        var dispatcher = new SessionGpuRequestDispatcher(manager);
        await using SessionAgentPipeOwner owner = AcquireOwner();
        await using var server = new SessionGpuPipeServer(owner, dispatcher);

        SessionGpuResponse failed = await ServeQueryAsync(server);
        SessionGpuResponse recovered = await ServeQueryAsync(server);

        Assert.Equal(SessionGpuOperationStatus.Failed, failed.Status);
        Assert.Contains("could not complete", failed.Error, StringComparison.Ordinal);
        Assert.Equal(SessionGpuOperationStatus.Succeeded, recovered.Status);
        Assert.Empty(recovered.Apps);
    }

    private static async Task<SessionGpuResponse> ServeQueryAsync(SessionGpuPipeServer server)
    {
        var request = new QueryGpuAppsRequest(
            SessionGpuIpc.ProtocolVersion,
            Guid.NewGuid());
        await using var requestBytes = new MemoryStream();
        await SessionGpuIpcCodec.WriteRequestAsync(requestBytes, request);
        await using var connection = new TestDuplexStream(requestBytes.ToArray());

        await server.ServeConnectionAsync(connection, CancellationToken.None);
        connection.RewindOutput();
        SessionGpuResponse response = await SessionGpuIpcCodec.ReadResponseAsync(connection);
        Assert.Equal(request.RequestId, response.RequestId);
        return response;
    }

    private static SessionAgentPipeOwner AcquireOwner()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionGpuIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        Assert.True(SessionAgentPipeOwner.TryAcquire(
            endpoint,
            sid,
            out SessionAgentPipeOwner? owner));
        return owner!;
    }

    private static async Task ConnectAndCloseAsync(string pipeName)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.ConnectAsync(timeout.Token);
    }

    private static async Task<SessionGpuResponse> QueryAsync(string pipeName)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.ConnectAsync(timeout.Token);
        var request = new QueryGpuAppsRequest(
            SessionGpuIpc.ProtocolVersion,
            Guid.NewGuid());
        await SessionGpuIpcCodec.WriteRequestAsync(client, request, timeout.Token);
        return await SessionGpuIpcCodec.ReadResponseAsync(client, timeout.Token);
    }

    private sealed class ThrowOnceGpuAppManager : IWindowsGpuAppManager
    {
        private int scans;

        public WindowsGpuAppScan Scan(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref scans) == 1)
            {
                throw new NullReferenceException("simulated dispatcher defect");
            }
            return new WindowsGpuAppScan(true, null, []);
        }

        public IReadOnlyList<WindowsGpuAppActionResult> CloseGracefully(
            IReadOnlyList<WindowsGpuApp> apps,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IReadOnlyList<WindowsGpuAppActionResult> ForceClose(
            IReadOnlyList<WindowsGpuApp> apps,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            (readOutput ? writer : reader).Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken token = default) =>
            (readOutput ? writer : reader).ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) =>
            writer.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken token = default) => writer.WriteAsync(buffer, token);
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken token) => writer.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
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
