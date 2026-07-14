using System.IO.Pipes;
using System.Security.Principal;
using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class NamedPipeStatusServerTests
{
    [Fact]
    public async Task ServeOnceAsync_RoundTripsStatusRequest()
    {
        string pipeName = $"OpenSynapse.Tests.{Guid.NewGuid():N}";
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows user has no SID.");
        NamedPipeStatusServer server = new(
            pipeName,
            user,
            new StatusRequestHandler(new FakeConflictDetector([])));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        ServiceRequest request = new(
            OpenSynapseIpc.ProtocolVersion,
            Guid.NewGuid(),
            "status");

        await IpcMessageCodec.WriteAsync(client, request, timeout.Token);
        ServiceResponse response = await IpcMessageCodec.ReadAsync<ServiceResponse>(
            client,
            timeout.Token);
        await serverTask;

        Assert.True(response.Success);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.NotNull(response.Status);
        Assert.True(response.Status.WritesBlocked);
    }

    private sealed class FakeConflictDetector(IReadOnlyList<string> conflicts) : IConflictDetector
    {
        public IReadOnlyList<string> GetRunningConflicts() => conflicts;
    }
}
