using System.Buffers.Binary;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentClient.Tests;

public sealed class BrokerPipeChannelTests
{
    [Fact]
    public async Task Diagnostics_UsesOnlyFixedReadActionAfterServerVerification()
    {
        byte[] expected = DeploymentStatusCodec.Serialize(new(1, RequestId, "Succeeded", "deployment-status",
            new(false, "None", null, [])));
        DuplexConnection connection = new(expected);
        RecordingIdentityVerifier verifier = new();
        byte[] response = await DeploymentStatusCommand.ReadAsync(new FixedConnector(connection), verifier,
            RequestId, CancellationToken.None);
        Assert.Equal(expected, response);
        Assert.Equal(connection.ServerIdentity, verifier.VerifiedIdentity);
        Assert.Equal(DeploymentBrokerAction.ReadDeploymentStatus,
            DeploymentBrokerProtocol.ParseRequest(connection.Written.ToArray().AsSpan(4)).Action);
    }

    [Fact]
    public async Task Diagnostics_RejectsUntrustedPeerBeforeAnyRequest()
    {
        DuplexConnection connection = new(BrokerProtocolTests.Success());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DeploymentStatusCommand.ReadAsync(
            new FixedConnector(connection), new RecordingIdentityVerifier { Reject = true }, RequestId, CancellationToken.None));
        Assert.Empty(connection.Written.ToArray());
    }

    [Fact]
    public async Task Diagnostics_RejectsMismatchedReplyIdentity()
    {
        DuplexConnection connection = new(DeploymentStatusCodec.Serialize(new(1, Guid.NewGuid(), "Succeeded", "deployment-status",
            new(false, "None", null, []))));
        await Assert.ThrowsAsync<InvalidDataException>(() => DeploymentStatusCommand.ReadAsync(new FixedConnector(connection),
            new RecordingIdentityVerifier(), RequestId, CancellationToken.None));
    }
    private static readonly Guid RequestId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channel_VerifiesPipeServerBeforeWritingFixedRequest(bool cpuVoltage)
    {
        DuplexConnection connection = new(BrokerProtocolTests.Success());
        RecordingIdentityVerifier verifier = new();
        BrokerPipeChannel channel = new(new FixedConnector(connection), verifier);

        DeploymentBrokerResponse response = cpuVoltage
            ? await channel.ApplyCpuVoltageInboxAsync(RequestId, CancellationToken.None)
            : await channel.ApplyFixedInboxAsync(RequestId, CancellationToken.None);

        Assert.Equal(BrokerProtocolTests.Success(), response);
        Assert.Equal(connection.ServerIdentity, verifier.VerifiedIdentity);
        byte[] frame = connection.Written.ToArray();
        int length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, sizeof(int)));
        Assert.Equal(frame.Length - sizeof(int), length);
        DeploymentBrokerRequest request = DeploymentBrokerProtocol.ParseRequest(frame.AsSpan(sizeof(int)));
        Assert.Equal(RequestId, request.RequestId);
        Assert.Equal(cpuVoltage ? DeploymentBrokerAction.ApplyCpuVoltageInbox : DeploymentBrokerAction.ApplyFixedInbox, request.Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channel_WritesNothingWhenServerIdentityFails(bool cpuVoltage)
    {
        DuplexConnection connection = new(BrokerProtocolTests.Success());
        RecordingIdentityVerifier verifier = new() { Reject = true };
        BrokerPipeChannel channel = new(new FixedConnector(connection), verifier);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => cpuVoltage ? channel.ApplyCpuVoltageInboxAsync(RequestId, CancellationToken.None)
                : channel.ApplyFixedInboxAsync(RequestId, CancellationToken.None));

        Assert.Empty(connection.Written.ToArray());
    }

    [Fact]
    public async Task Channel_UsesLongTerminalDeadlineAfterShortPreflightDeadlineEnds()
    {
        DuplexConnection connection = new(BrokerProtocolTests.Success());
        RecordingDeadlineFactory deadlines = new();
        BrokerPipeChannel channel = new(
            new FixedConnector(connection),
            new RecordingIdentityVerifier(),
            deadlines);

        DeploymentBrokerResponse response = await channel.ApplyFixedInboxAsync(
            RequestId,
            CancellationToken.None);

        Assert.Equal(BrokerProtocolTests.Success(), response);
        Assert.Equal(
            [BrokerPipeChannel.PreflightTimeout, BrokerPipeChannel.TerminalResponseTimeout],
            deadlines.Timeouts);
        Assert.Equal(TimeSpan.FromSeconds(10), deadlines.Timeouts[0]);
        Assert.Equal(TimeSpan.FromMinutes(40), deadlines.Timeouts[1]);
        Assert.True(deadlines.PreflightDisposedBeforeTerminalCreated);
    }

    private sealed class FixedConnector(IBrokerPipeConnection connection) : IBrokerPipeConnector
    {
        public Task<IBrokerPipeConnection> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(connection);
    }

    private sealed class RecordingIdentityVerifier : IBrokerServerIdentityVerifier
    {
        internal bool Reject { get; init; }

        internal BrokerServerIdentity? VerifiedIdentity { get; private set; }

        public void Verify(BrokerServerIdentity identity)
        {
            VerifiedIdentity = identity;
            if (Reject)
            {
                throw new UnauthorizedAccessException("synthetic rejected identity");
            }
        }
    }

    private sealed class RecordingDeadlineFactory : IBrokerDeadlineFactory
    {
        private RecordingDeadline? preflight;

        internal List<TimeSpan> Timeouts { get; } = [];

        internal bool PreflightDisposedBeforeTerminalCreated { get; private set; }

        public IBrokerDeadline Create(CancellationToken cancellationToken, TimeSpan timeout)
        {
            Timeouts.Add(timeout);
            if (Timeouts.Count == 2)
            {
                PreflightDisposedBeforeTerminalCreated = preflight?.Disposed == true;
            }

            RecordingDeadline result = new(cancellationToken);
            preflight ??= result;
            return result;
        }
    }

    private sealed class RecordingDeadline(CancellationToken token) : IBrokerDeadline
    {
        public CancellationToken Token { get; } = token;

        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class DuplexConnection : IBrokerPipeConnection
    {
        private readonly MemoryStream response;

        internal DuplexConnection(DeploymentBrokerResponse brokerResponse)
            : this(DeploymentBrokerProtocol.SerializeResponse(brokerResponse))
        {
        }

        internal DuplexConnection(byte[] payload)
        {
            byte[] frame = new byte[sizeof(int) + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame.AsSpan(sizeof(int)));
            response = new MemoryStream(frame, writable: false);
        }

        public BrokerServerIdentity ServerIdentity { get; } = new(
            PipeServerProcessId: 1234,
            ServiceProcessId: 1234,
            ServiceIsRunning: true,
            ServiceName: "OpenBlade Local Dev Deployment Broker",
            BrokerServerIdentityPolicy.ServiceWin32OwnProcess,
            BrokerServerIdentityPolicy.ServiceDemandStart,
            "\"C:\\Program Files\\OpenBlade Local Dev Deployment Broker\\OpenBlade.DeploymentBroker.exe\"",
            StartName: "LocalSystem");

        public Stream Input => response;

        public Stream Output => Written;

        internal MemoryStream Written { get; } = new();

        public ValueTask DisposeAsync()
        {
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
