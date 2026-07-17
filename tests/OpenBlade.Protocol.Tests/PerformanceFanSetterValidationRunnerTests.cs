using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PerformanceFanSetterValidationRunnerTests
{
    public static TheoryData<
        PerformanceFanValidationTarget,
        string,
        string,
        string[]> SuccessfulTargets => new()
    {
        {
            PerformanceFanValidationTarget.Balanced,
            "01010500",
            "01020500",
            ["01010000", "01020000"]
        },
        {
            PerformanceFanValidationTarget.Silent,
            "01010000",
            "01020000",
            ["01010500", "01020500"]
        },
        {
            PerformanceFanValidationTarget.Performance,
            "01010000",
            "01020000",
            ["01010200", "01020200"]
        },
        {
            PerformanceFanValidationTarget.Fixed5400,
            "01010000",
            "01020000",
            ["01010001", "01020001", "010136", "010236"]
        },
    };

    [Theory]
    [MemberData(nameof(SuccessfulTargets))]
    public async Task RunAsync_AppliesReadsBackAndRestoresTypedBaseline(
        PerformanceFanValidationTarget target,
        string baseline1,
        string baseline2,
        string[] expectedCandidateWrites)
    {
        var session = new StatefulSession(baseline1, baseline2);

        PerformanceFanSetterValidationResult result =
            await Runner(session).RunAsync(target, default);

        Assert.True(result.Success);
        Assert.True(result.ApplyAcknowledged);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.Restored);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(baseline1, result.Baseline!.Thermal1Hex);
        Assert.Equal(baseline2, result.Restoration!.Thermal2Hex);

        string[] writes = session.Commands
            .Where(command => command.CommandId is 0x01 or 0x02)
            .Select(command => Convert.ToHexString(command.Arguments.Span))
            .ToArray();
        Assert.Equal(expectedCandidateWrites, writes[..expectedCandidateWrites.Length]);
        Assert.Equal([baseline1, baseline2], writes[^2..]);

        if (target == PerformanceFanValidationTarget.Fixed5400)
        {
            Assert.Equal("01010001", result.Candidate!.Thermal1Hex);
            Assert.Equal("01020001", result.Candidate.Thermal2Hex);
            Assert.Equal(new FanRpmReadings(5300, 5300), result.CandidateFanRpm);
            Assert.Equal([0x02, 0x02, 0x01, 0x01],
                session.Commands
                    .Where(command => command.CommandId is 0x01 or 0x02)
                    .Take(4)
                    .Select(command => command.CommandId)
                    .ToArray());
        }
        else
        {
            Assert.Null(result.CandidateFanRpm);
            Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x88);
        }
    }

    [Theory]
    [InlineData(4900, 5300)]
    [InlineData(5300, 6100)]
    public async Task RunAsync_FixedFanRpmOutsideHighRangeFailsAfterRestoration(
        int firstRpm,
        int secondRpm)
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            firstFanRpm: firstRpm,
            secondFanRpm: secondRpm);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Fixed5400, default);

        Assert.Equal(
            PerformanceFanValidationFailure.FixedFanRpmOutOfRange,
            result.Failure);
        Assert.Equal(new FanRpmReadings(firstRpm, secondRpm), result.CandidateFanRpm);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_FixedFanRpmReadFailureStillRestoresConfirmedState()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            failFanSelector: 2);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Fixed5400, default);

        Assert.Equal(
            PerformanceFanValidationFailure.FixedFanRpmReadFailed,
            result.Failure);
        Assert.Null(result.CandidateFanRpm);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_AbortsWithoutWritesWhenTargetIsAlreadyActive()
    {
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Balanced, default);

        Assert.Equal(PerformanceFanValidationFailure.TargetAlreadyActive, result.Failure);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x01 or 0x02);
    }

    [Theory]
    [InlineData("01010001", "01020001")]
    [InlineData("01010401", "01020401")]
    [InlineData("01010300", "01020300")]
    [InlineData("01010000", "01020200")]
    public async Task RunAsync_FailsClosedForFixedUnknownOrMismatchedBaseline(
        string baseline1,
        string baseline2)
    {
        var session = new StatefulSession(baseline1, baseline2);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Silent, default);

        Assert.False(result.Success);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x01 or 0x02);
        Assert.Contains(result.Failure, new[]
        {
            PerformanceFanValidationFailure.BaselineFixedSpeedCannotBeRestored,
            PerformanceFanValidationFailure.BaselineReadFailed,
            PerformanceFanValidationFailure.BaselinePairMismatch,
        });
    }

    [Fact]
    public async Task RunAsync_DoesNotRollbackAfterIndeterminatePartialWrite()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            failWriteNumber: 2);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Performance, default);

        Assert.Equal(
            PerformanceFanValidationFailure.ApplyWriteIndeterminate,
            result.Failure);
        Assert.True(result.ApplyAttempted);
        Assert.False(result.ApplyAcknowledged);
        Assert.False(result.RestoreAttempted);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
    }

    private static PerformanceFanSetterValidationRunner Runner(
        IRazerCommandSession session) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        new PendingDisposalRegistry(),
        TimeSpan.Zero);

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class StatefulSession(
        string firstHex,
        string secondHex,
        int? failWriteNumber = null,
        int firstFanRpm = 5300,
        int secondFanRpm = 5300,
        int? failFanSelector = null) : IRazerCommandSession
    {
        private readonly byte[][] state =
            [Convert.FromHexString(firstHex), Convert.FromHexString(secondHex)];
        private int writeCount;

        internal List<RazerCommandSpec> Commands { get; } = [];

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            Commands.Add(command);
            if (command.CommandId == 0x82)
            {
                int index = command.Arguments.Span[1] - 1;
                byte[] response = state[index].ToArray();
                if (command.PayloadValidator is not null
                    && !command.PayloadValidator(response))
                {
                    return Failure(HidSessionFailureKind.ProtocolFailure);
                }

                return Success(response);
            }

            if (command.CommandId == 0x88)
            {
                int selector = command.Arguments.Span[1];
                if (selector == failFanSelector)
                {
                    return Failure(HidSessionFailureKind.Timeout);
                }

                int rpm = selector == 1 ? firstFanRpm : secondFanRpm;
                byte[] response = [0x01, (byte)selector, checked((byte)(rpm / 100))];
                return Success(response);
            }

            writeCount++;
            if (writeCount == failWriteNumber)
            {
                return Failure(HidSessionFailureKind.Timeout);
            }

            byte[] payload = command.Arguments.ToArray();
            if (command.PayloadValidator is not null
                && !command.PayloadValidator(payload))
            {
                return Failure(HidSessionFailureKind.ProtocolFailure);
            }

            if (command.CommandId == 0x02)
            {
                int index = payload[1] - 1;
                state[index] = payload.ToArray();
            }

            return Success(payload);
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Success(byte[] payload) =>
            Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Failure(
            HidSessionFailureKind failure) =>
            Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, failure));
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
