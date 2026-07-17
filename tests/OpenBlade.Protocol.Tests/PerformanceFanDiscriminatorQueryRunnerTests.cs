using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PerformanceFanDiscriminatorQueryRunnerTests
{
    [Fact]
    public async Task RunAsync_SendsOnlySixFixedQueriesAndDisposesSession()
    {
        var session = new RecordingSession();
        var runner = Runner(session);

        HidSessionResult<PerformanceFanDiscriminatorSnapshot> result =
            await runner.RunAsync(default);

        Assert.Null(result.Failure);
        Assert.Equal([1, 2, 3, 4, 5, 6], session.TransactionIds);
        Assert.Equal([0x8E, 0x8F, 0x80, 0x89, 0x8B, 0x8A],
            session.Commands.Select(command => command.CommandId).ToArray());
        Assert.All(session.Commands, command => Assert.Equal(0x0D, command.ClassId));
        Assert.Equal([1, 1, 80, 2, 2, 2],
            session.Commands.Select(command => command.Arguments.Length).ToArray());
        Assert.All(session.Commands.SelectMany(command => command.Arguments.ToArray()),
            value => Assert.Equal(0, value));
        Assert.Equal(["0D/8E", "0D/8F", "0D/80", "0D/89", "0D/8B", "0D/8A"],
            result.Value!.Readings.Select(reading => reading.Operation).ToArray());
        Assert.Equal(160, result.Value.Readings[2].RequestPayloadHex.Length);
        Assert.Equal(160, result.Value.Readings[2].ResponseHex.Length);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(
            HidSessionFailureKind.Disposed,
            (await runner.RunAsync(default)).Failure);
    }

    [Fact]
    public async Task RunAsync_StopsAtFirstFailureAndPreservesDiagnostic()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.ProtocolValidation,
            HidSessionFailureKind.ProtocolFailure,
            null,
            null)
        {
            ProtocolValidationFailure = HidProtocolValidationFailure.Checksum,
        };
        var session = new RecordingSession(failAtIndex: 2, diagnostic);

        HidSessionResult<PerformanceFanDiscriminatorSnapshot> result =
            await Runner(session).RunAsync(default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Equal([0x8E, 0x8F, 0x80],
            session.Commands.Select(command => command.CommandId).ToArray());
        Assert.Equal(1, session.DisposeCount);
    }

    private static PerformanceFanDiscriminatorQueryRunner Runner(
        IRazerCommandSession session) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        new PendingDisposalRegistry());

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class RecordingSession(
        int? failAtIndex = null,
        HidSessionFailureDiagnostic? diagnostic = null) : IRazerCommandSession
    {
        internal List<byte> TransactionIds { get; } = [];
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal int DisposeCount { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            TransactionIds.Add(assignTransactionId().TransactionId);
            Commands.Add(command);
            if (Commands.Count - 1 == failAtIndex)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                    default,
                    HidSessionFailureKind.ProtocolFailure)
                {
                    Diagnostic = diagnostic,
                });
            }

            byte[] payload = Enumerable.Repeat(
                command.CommandId,
                command.ExpectedPayloadLength).ToArray();
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnsupportedBatteryCommands : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnsupportedTemporaryCommands : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
