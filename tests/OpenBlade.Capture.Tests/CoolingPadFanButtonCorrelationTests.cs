using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanButtonCorrelationTests
{
    [Fact]
    public async Task Runner_UsesOneSessionAndExactTwelveQueryOrder()
    {
        var responses = Enumerable.Range(0, CoolingPadFanButtonCorrelationRunner.Stages.Count)
            .SelectMany(index => new byte[][]
            {
                [0x01, 0x05, (byte)index],
                [0x00, 0x06],
                [0x01, 0x02, 0x2C],
            })
            .ToArray();
        var session = new RecordingSession(responses);
        var actorFactory = new ActorFactory(CreateActor(session));
        var confirmations = new List<CoolingPadFanButtonCorrelationStage>();
        var runner = CreateRunner(actorFactory);

        CoolingPadFanButtonCorrelationResult result = await runner.RunAsync(
            (stage, _) =>
            {
                confirmations.Add(stage);
                return Task.FromResult(true);
            },
            default);

        Assert.True(result.Success);
        Assert.Equal(CoolingPadFanButtonCorrelationRunner.Stages, confirmations);
        Assert.Equal(4, result.Snapshots.Count);
        Assert.Equal(12, result.QueryCount);
        Assert.True(result.OneSessionConfirmed);
        Assert.True(result.ReadOnly);
        Assert.Equal(0, result.OpenBladeWriteCount);
        Assert.False(result.ActiveModeReadbackConfirmed);
        Assert.False(result.TachometerReadbackConfirmed);
        Assert.False(result.ProductionWriteAdmitted);
        Assert.Equal(1, actorFactory.OpenCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(Enumerable.Range(1, 12).Select(value => (byte)value), session.TransactionIds);

        for (int stageIndex = 0; stageIndex < 4; stageIndex++)
        {
            AssertCommand(
                session.Commands[(stageIndex * 3) + 0],
                0x81,
                [0x01, 0x05, 0x00]);
            AssertCommand(
                session.Commands[(stageIndex * 3) + 1],
                0x90,
                [0x00, 0x00, 0x00]);
            AssertCommand(
                session.Commands[(stageIndex * 3) + 2],
                0x90,
                [0x01, 0x00, 0x00]);
            CoolingPadFanButtonSnapshot snapshot = result.Snapshots[stageIndex];
            Assert.Equal(CoolingPadFanButtonCorrelationRunner.Stages[stageIndex], snapshot.Stage);
            Assert.Equal(stageIndex, snapshot.SequenceIndex);
            Assert.Equal((byte)stageIndex, snapshot.OpaqueObservation);
            Assert.Equal($"0105{stageIndex:X2}", snapshot.OpaqueObservationPayloadHex);
            Assert.Equal("0006", snapshot.ConfigurationBank0PayloadHex);
            Assert.Equal("01022C", snapshot.ConfigurationBank1PayloadHex);
        }

        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x01 or 0x10 or 0x88);
    }

    [Fact]
    public async Task Runner_BaselineRejectionDoesNotOpenDevice()
    {
        var session = new RecordingSession([]);
        var actorFactory = new ActorFactory(CreateActor(session));
        var runner = CreateRunner(actorFactory);

        CoolingPadFanButtonCorrelationResult result = await runner.RunAsync(
            static (_, _) => Task.FromResult(false),
            default);

        Assert.Equal(
            CoolingPadFanButtonCorrelationFailure.ConfirmationRejected,
            result.Failure);
        Assert.Empty(result.Snapshots);
        Assert.Equal(0, result.QueryCount);
        Assert.False(result.OneSessionConfirmed);
        Assert.Equal(0, actorFactory.OpenCount);
        Assert.Empty(session.Commands);
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_ExhaustedTransactionBudgetDoesNotOpenDevice()
    {
        var context = new ValidationRunContext();
        for (int index = 0; index < byte.MaxValue; index++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }

        var session = new RecordingSession([]);
        var actorFactory = new ActorFactory(CreateActor(session));
        var runner = new CoolingPadFanButtonCorrelationRunner(
            () => context,
            actorFactory,
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry());

        CoolingPadFanButtonCorrelationResult result = await runner.RunAsync(
            static (_, _) => Task.FromResult(true),
            default);

        Assert.Equal(
            CoolingPadFanButtonCorrelationFailure.TransactionBudgetUnavailable,
            result.Failure);
        Assert.Equal(0, actorFactory.OpenCount);
        Assert.Empty(session.Commands);
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_QueryFailureStopsWithoutRetryOrLaterPrompt()
    {
        byte[][] responses =
        [
            [0x01, 0x05, 0x00],
            [0x00, 0x06],
            [0x01, 0x02, 0x2C],
            [0x01, 0x05, 0x01],
            [0x00, 0x06],
        ];
        var session = new RecordingSession(responses, failureIndex: 4);
        var actorFactory = new ActorFactory(CreateActor(session));
        var confirmations = new List<CoolingPadFanButtonCorrelationStage>();
        var runner = CreateRunner(actorFactory);

        CoolingPadFanButtonCorrelationResult result = await runner.RunAsync(
            (stage, _) =>
            {
                confirmations.Add(stage);
                return Task.FromResult(true);
            },
            default);

        Assert.Equal(CoolingPadFanButtonCorrelationFailure.QueryFailed, result.Failure);
        Assert.Equal(HidSessionFailureKind.Timeout, result.SessionFailure);
        Assert.Equal(
            [CoolingPadFanButtonCorrelationStage.LowBaseline,
                CoolingPadFanButtonCorrelationStage.Medium],
            confirmations);
        Assert.Single(result.Snapshots);
        Assert.Equal(5, result.QueryCount);
        Assert.Equal(5, session.Commands.Count);
        Assert.Equal(1, actorFactory.OpenCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_IsOneShot()
    {
        byte[][] responses = Enumerable.Range(
                0,
                CoolingPadFanButtonCorrelationRunner.Stages.Count)
            .SelectMany(_ => new byte[][]
            {
                [0x01, 0x05, 0x00],
                [0x00, 0x06],
                [0x01, 0x02, 0x2C],
            })
            .ToArray();
        var session = new RecordingSession(responses);
        var actorFactory = new ActorFactory(CreateActor(session));
        var runner = CreateRunner(actorFactory);

        CoolingPadFanButtonCorrelationResult first = await runner.RunAsync(
            static (_, _) => Task.FromResult(true),
            default);
        CoolingPadFanButtonCorrelationResult second = await runner.RunAsync(
            static (_, _) => Task.FromResult(true),
            default);

        Assert.True(first.Success);
        Assert.Equal(CoolingPadFanButtonCorrelationFailure.AlreadyRun, second.Failure);
        Assert.Equal(1, actorFactory.OpenCount);
        Assert.Equal(12, session.Commands.Count);
    }

    [Fact]
    public async Task Command_RequiresExactTokenAndEmitsSanitizedReadOnlyResult()
    {
        var output = new StringWriter();
        var promptedStages = new List<CoolingPadFanButtonCorrelationStage>();
        var command = new CoolingPadFanButtonCorrelationCommand(
            async (confirm, cancellationToken) =>
            {
                foreach (CoolingPadFanButtonCorrelationStage stage in
                    CoolingPadFanButtonCorrelationRunner.Stages)
                {
                    Assert.True(await confirm(stage, cancellationToken));
                    promptedStages.Add(stage);
                }

                return SuccessfulResult();
            },
            new StringReader(string.Join(
                Environment.NewLine,
                "LOW-BASELINE-CONFIRMED",
                "MEDIUM-AFTER-PRESS-1",
                "HIGH-AFTER-PRESS-2",
                "LOW-AFTER-PRESS-3")),
            output);

        int exitCode = await command.RunAsync(
            ["--confirm-target", CoolingPadFanButtonCorrelationCommand.Confirmation],
            default);

        string text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(CoolingPadFanButtonCorrelationRunner.Stages, promptedStages);
        Assert.Contains("\"readOnly\": true", text, StringComparison.Ordinal);
        Assert.Contains("\"openBladeWriteCount\": 0", text, StringComparison.Ordinal);
        Assert.Contains(
            "\"observationSemantics\": \"OpaqueByteAndStoredConfigurationBanks\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains("\"configuredTargetReadbackConfirmed\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"activeModeReadbackConfirmed\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"tachometerReadbackConfirmed\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"productionWriteAdmitted\": false", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", text, StringComparison.OrdinalIgnoreCase);

        var rejectedOutput = new StringWriter();
        var rejected = new CoolingPadFanButtonCorrelationCommand(
            static (_, _) => throw new InvalidOperationException("must not run"),
            TextReader.Null,
            rejectedOutput);
        Assert.Equal(2, await rejected.RunAsync(["--confirm-target", "wrong"], default));
        Assert.Contains("Usage:", rejectedOutput.ToString(), StringComparison.Ordinal);
    }

    private static CoolingPadFanButtonCorrelationResult SuccessfulResult()
    {
        CoolingPadFanButtonSnapshot[] snapshots =
            CoolingPadFanButtonCorrelationRunner.Stages
                .Select((stage, index) => new CoolingPadFanButtonSnapshot(
                    stage,
                    index,
                    index,
                    "010500",
                    0,
                    "0006",
                    "01022C"))
                .ToArray();
        return new CoolingPadFanButtonCorrelationResult(
            snapshots,
            12,
            OneSessionConfirmed: true,
            ReadOnly: true,
            OpenBladeWriteCount: 0,
            ActiveModeReadbackConfirmed: false,
            TachometerReadbackConfirmed: false,
            ProductionWriteAdmitted: false,
            SessionFailure: null,
            Failure: CoolingPadFanButtonCorrelationFailure.None);
    }

    private static CoolingPadFanButtonCorrelationRunner CreateRunner(
        IValidationActorFactory actorFactory) => new(
        static () => new ValidationRunContext(),
        actorFactory,
        TimeProvider.System,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        new PendingDisposalRegistry());

    private static ValidationActor CreateActor(IRazerCommandSession session) => new(
        session,
        new UnsupportedBatteryCommands(),
        new UnsupportedTemporaryCommands());

    private static void AssertCommand(
        RazerCommandSpec command,
        byte expectedCommandId,
        byte[] expectedArguments)
    {
        Assert.Equal(0x0D, command.ClassId);
        Assert.Equal(expectedCommandId, command.CommandId);
        Assert.Equal(expectedArguments, command.Arguments.ToArray());
    }

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        internal int OpenCount { get; private set; }

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            OpenCount++;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
        }
    }

    private sealed class RecordingSession(
        IReadOnlyList<byte[]> responses,
        int? failureIndex = null) : IRazerCommandSession
    {
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<byte> TransactionIds { get; } = [];
        internal int DisposeCount { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            int index = Commands.Count;
            Commands.Add(command);
            TransactionIdAssignment transaction = assignTransactionId();
            Assert.Null(transaction.Failure);
            TransactionIds.Add(transaction.TransactionId);
            if (failureIndex == index)
            {
                return Task.FromResult(
                    new HidSessionResult<ReadOnlyMemory<byte>>(
                        default,
                        HidSessionFailureKind.Timeout));
            }

            byte[] response = responses[index];
            Assert.Equal(command.ExpectedPayloadLength, response.Length);
            Assert.True(command.PayloadValidator?.Invoke(response) ?? true);
            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
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

    private sealed class UnsupportedTemporaryCommands
        : ITemporaryFullChargeCommandClient
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
