using System.IO.Pipes;
using System.Security.Principal;
using System.Buffers.Binary;
using System.Text;
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

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public async Task ServeOnceAsync_ReturnsErrorForMalformedAuthorizedRequest(byte[] message)
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

        await client.WriteAsync(message, timeout.Token);
        await client.FlushAsync(timeout.Token);
        ServiceResponse response = await IpcMessageCodec.ReadAsync<ServiceResponse>(
            client,
            timeout.Token);
        await serverTask;

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
    }

    public static TheoryData<byte[]> InvalidMessages()
    {
        TheoryData<byte[]> data = [];
        data.Add(Frame(Encoding.UTF8.GetBytes("null")));
        data.Add(Frame(Encoding.UTF8.GetBytes("{not-json")));
        data.Add(Frame(Encoding.UTF8.GetBytes(
            "{\"version\":1,\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"operation\":null}")));

        byte[] oversizedPrefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            oversizedPrefix,
            OpenSynapseIpc.MaximumMessageBytes + 1);
        data.Add(oversizedPrefix);
        return data;
    }

    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private sealed class FakeConflictDetector(IReadOnlyList<string> conflicts) : IConflictDetector
    {
        public ConflictCheck Check() => new(conflicts, null);
    }
}
