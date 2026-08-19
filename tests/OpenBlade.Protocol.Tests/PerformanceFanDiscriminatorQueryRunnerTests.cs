using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PerformanceFanDiscriminatorQueryRunnerTests
{
    [Fact]
    public async Task RunAsync_SendsOnlyCapturedOrProductSourceBackedReadOnlyQueriesAndDisposesSession()
    {
        var session = new RecordingSession();
        var runner = Runner(session);

        HidSessionResult<PerformanceFanDiscriminatorSnapshot> result =
            await runner.RunAsync(default);

        Assert.Null(result.Failure);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], session.TransactionIds);
        Assert.Equal([0x8F, 0x82, 0x82, 0x87, 0x87, 0x98, 0x87, 0x8C],
            session.Commands.Select(command => command.CommandId).ToArray());
        Assert.Equal([0x07, 0x0D, 0x0D, 0x0D, 0x0D, 0x0F, 0x00, 0x07],
            session.Commands.Select(command => command.ClassId).ToArray());
        Assert.Equal([1, 4, 4, 3, 3, 1, 4, 2],
            session.Commands.Select(command => command.Arguments.Length).ToArray());
        Assert.Equal([0x01, 0x01, 0x00, 0x00], session.Commands[1].Arguments.ToArray());
        Assert.Equal([0x01, 0x02, 0x00, 0x00], session.Commands[2].Arguments.ToArray());
        Assert.Equal([0x00], session.Commands[0].Arguments.ToArray());
        Assert.Equal([0x01, 0x01, 0x00], session.Commands[3].Arguments.ToArray());
        Assert.Equal([0x01, 0x02, 0x00], session.Commands[4].Arguments.ToArray());
        Assert.Equal([0x00], session.Commands[5].Arguments.ToArray());
        Assert.Equal([0x00, 0x00, 0x00, 0x00], session.Commands[6].Arguments.ToArray());
        Assert.Equal([0x00, 0x00], session.Commands[7].Arguments.ToArray());
        Assert.Equal(
            [
                "07/8F power control",
                "0D/82 class1 thermal1",
                "0D/82 class1 thermal2",
                "0D/87 custom CPU component",
                "0D/87 custom GPU component",
                "0F/98 startup animation candidate",
                "00/87 MCU version",
                "07/8C adapter power",
            ],
            result.Value!.Readings.Select(reading => reading.Operation).ToArray());
        Assert.Equal(8, result.Value.Readings[1].RequestPayloadHex.Length);
        Assert.Equal(8, result.Value.Readings[1].ResponseHex.Length);
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
        Assert.Equal([0x8F, 0x82, 0x82],
            session.Commands.Select(command => command.CommandId).ToArray());
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task PowerSourceOnly_SendsAdapterAndTwoThermalQueries()
    {
        var session = new RecordingSession();

        HidSessionResult<PerformanceFanDiscriminatorSnapshot> result =
            await Runner(session, powerSourceOnly: true).RunAsync(default);

        Assert.Null(result.Failure);
        Assert.Equal([1, 2, 3], session.TransactionIds);
        Assert.Equal(
            [(byte)0x8C, (byte)0x82, (byte)0x82],
            session.Commands.Select(command => command.CommandId).ToArray());
        Assert.Equal(
            [
                "07/8C adapter power",
                "0D/82 class1 thermal1",
                "0D/82 class1 thermal2",
            ],
            result.Value!.Readings.Select(reading => reading.Operation).ToArray());
        Assert.Equal([0x00, 0x00], session.Commands[0].Arguments.ToArray());
        Assert.Equal([0x01, 0x01, 0x00, 0x00], session.Commands[1].Arguments.ToArray());
        Assert.Equal([0x01, 0x02, 0x00, 0x00], session.Commands[2].Arguments.ToArray());
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task HyperBoostObservationOnly_SendsStrictThermalPair()
    {
        var session = new RecordingSession();

        HidSessionResult<PerformanceFanDiscriminatorSnapshot> result =
            await Runner(session, hyperBoostObservationOnly: true).RunAsync(default);

        Assert.Null(result.Failure);
        Assert.Equal([1, 2], session.TransactionIds);
        Assert.All(session.Commands, command =>
        {
            Assert.Equal(0x0D, command.ClassId);
            Assert.Equal(0x82, command.CommandId);
            Assert.Equal(4, command.ExpectedPayloadLength);
            Assert.NotNull(command.PayloadValidator);
        });
        Assert.Equal([0x01, 0x01, 0x00, 0x00], session.Commands[0].Arguments.ToArray());
        Assert.Equal([0x01, 0x02, 0x00, 0x00], session.Commands[1].Arguments.ToArray());
        Assert.True(session.Commands[0].PayloadValidator!(
            new byte[] { 0x01, 0x01, 0x07, 0x00 }));
        Assert.True(session.Commands[1].PayloadValidator!(
            new byte[] { 0x01, 0x02, 0x07, 0x00 }));
        Assert.False(session.Commands[0].PayloadValidator!(
            new byte[] { 0x01, 0x02, 0x07, 0x00 }));
        Assert.False(session.Commands[1].PayloadValidator!(
            new byte[] { 0x01, 0x02, 0x07, 0x01 }));
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void NarrowedQuerySets_AreMutuallyExclusive()
    {
        var session = new RecordingSession();

        Assert.Throws<ArgumentException>(() => new PerformanceFanDiscriminatorQueryRunner(
            static () => new ValidationRunContext(),
            new ActorFactory(new ValidationActor(
                session,
                new UnsupportedBatteryCommands(),
                new UnsupportedTemporaryCommands())),
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry(),
            powerSourceOnly: true,
            hyperBoostObservationOnly: true));
    }

    private static PerformanceFanDiscriminatorQueryRunner Runner(
        IRazerCommandSession session,
        bool powerSourceOnly = false,
        bool hyperBoostObservationOnly = false) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        new PendingDisposalRegistry(),
        powerSourceOnly,
        hyperBoostObservationOnly);

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
