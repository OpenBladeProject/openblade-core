using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class GetterCandidateProbeRunnerTests
{
    [Fact]
    public async Task FunctionKeyRunner_SendsOnlyTheFixedCandidateAndDisposes()
    {
        var session = new RecordingSession();
        var runner = FunctionKeyRunner(session);

        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await runner.RunAsync(default);

        Assert.Null(result.Failure);
        Assert.Equal("0101", result.Value!.ResponsePayloadHex);
        Assert.Equal([1], session.TransactionIds);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal(0x02, command.ClassId);
        Assert.Equal(0x86, command.CommandId);
        Assert.Equal([0x01, 0x00], command.Arguments.ToArray());
        Assert.Equal(2, command.ExpectedPayloadLength);
        Assert.Equal(1, session.DisposeCount);

        HidSessionResult<GetterCandidateProbeSnapshot> second =
            await runner.RunAsync(default);
        Assert.Equal(HidSessionFailureKind.Disposed, second.Failure);
        Assert.Single(session.Commands);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task GamingModeRunner_SendsOnlyTheFixedCandidate()
    {
        var session = new RecordingSession(response: [0x01, 0x00, 0x00, 0x00]);

        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await GamingModeRunner(session).RunAsync(default);

        Assert.Null(result.Failure);
        Assert.Equal("01000000", result.Value!.ResponsePayloadHex);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal(0x00, command.ClassId);
        Assert.Equal(0x88, command.CommandId);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], command.Arguments.ToArray());
        Assert.Equal(4, command.ExpectedPayloadLength);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_PreservesFirstFailureAndDoesNotRetry()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.ProtocolValidation,
            HidSessionFailureKind.ProtocolFailure,
            null,
            null)
        {
            ProtocolValidationFailure = HidProtocolValidationFailure.Status,
        };
        var session = new RecordingSession(
            failure: HidSessionFailureKind.ProtocolFailure,
            diagnostic: diagnostic);

        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await FunctionKeyRunner(session).RunAsync(default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Single(session.Commands);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_CanceledBeforeOpenPerformsNoIo()
    {
        var session = new RecordingSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await FunctionKeyRunner(session).RunAsync(cancellation.Token);

        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Empty(session.Commands);
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public void CandidateTypes_AreAbsentFromTheProductionProtocolAssembly()
    {
        string[] names = typeof(TargetBladeDevice).Assembly.GetTypes()
            .Select(type => type.FullName ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(names, name =>
            name.Contains("GetterCandidate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x02, 0x86, "0100", 0x02, 0x87)]
    [InlineData(0x00, 0x88, "00000000", 0x04, 0x8C)]
    public void CandidateEnvelope_IsTheReviewedNinetyOneByteFeatureReport(
        byte commandClass,
        byte commandId,
        string payloadHex,
        byte payloadLength,
        byte checksum)
    {
        byte[] envelope = RazerReport.Create(
            transactionId: 1,
            commandClass,
            commandId,
            Convert.FromHexString(payloadHex)).Encode();
        byte[] featureReport = new byte[RazerReport.ReportLength + 1];
        envelope.CopyTo(featureReport, 1);

        Assert.Equal(91, featureReport.Length);
        Assert.Equal(0x00, featureReport[0]);
        Assert.Equal(0x00, featureReport[1]);
        Assert.Equal(0x01, featureReport[2]);
        Assert.Equal(payloadLength, featureReport[6]);
        Assert.Equal(commandClass, featureReport[7]);
        Assert.Equal(commandId, featureReport[8]);
        Assert.Equal(payloadHex, Convert.ToHexString(
            featureReport.AsSpan(9, payloadLength)));
        Assert.Equal(checksum, featureReport[89]);
        Assert.Equal(0x00, featureReport[90]);
    }

    private static FunctionKeyPrimaryGetterCandidateProbeRunner FunctionKeyRunner(
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

    private static GamingModeGetterCandidateProbeRunner GamingModeRunner(
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
        byte[]? response = null,
        HidSessionFailureKind? failure = null,
        HidSessionFailureDiagnostic? diagnostic = null) : IRazerCommandSession
    {
        private readonly byte[] response = response ?? [0x01, 0x01];

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
            return Task.FromResult(failure is null
                ? new HidSessionResult<ReadOnlyMemory<byte>>(response, null)
                : new HidSessionResult<ReadOnlyMemory<byte>>(default, failure)
                {
                    Diagnostic = diagnostic,
                });
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

        public Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
