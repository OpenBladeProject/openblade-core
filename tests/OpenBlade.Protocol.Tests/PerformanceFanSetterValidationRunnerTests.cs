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
        {
            PerformanceFanValidationTarget.BatterySaver,
            "01010600",
            "01020600",
            ["01010300", "01020300"]
        },
        {
            PerformanceFanValidationTarget.BatteryBalanced,
            "01010300",
            "01020300",
            ["01010600", "01020600"]
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
            Assert.Equal(3, result.CandidateFanRpmSamples.Count);
            Assert.Equal(6, session.Commands.Count(command => command.CommandId == 0x88));
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
            Assert.Empty(result.CandidateFanRpmSamples);
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
        Assert.Equal(3, result.CandidateFanRpmSamples.Count);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(6, session.Commands.Count(command => command.CommandId == 0x88));
    }

    [Fact]
    public async Task RunAsync_FixedFanRequiresThreeConsecutiveHighSamples()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            fanRpmSamples:
            [
                new(5100, 5200),
                new(5200, 5300),
                new(4900, 5300),
                new(5100, 5100),
                new(5200, 5200),
                new(5300, 5300),
            ]);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            maximumSamples: 6,
            stateCheckEverySamples: 5).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.True(result.Success);
        Assert.Equal(new FanRpmReadings(5300, 5300), result.CandidateFanRpm);
        Assert.Equal(
            [
                new(5100, 5200),
                new(5200, 5300),
                new(4900, 5300),
                new(5100, 5100),
                new(5200, 5200),
                new(5300, 5300),
            ],
            result.CandidateFanRpmSamples);
        Assert.Equal(12, session.Commands.Count(command => command.CommandId == 0x88));
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_FixedFanStateLossDuringRampFailsAndRestores()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            firstFanRpm: 3000,
            secondFanRpm: 3000,
            loseFixedStateAfterCompletedFanSamples: 1);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            maximumSamples: 3,
            stateCheckEverySamples: 1).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.Equal(
            PerformanceFanValidationFailure.CandidateTargetMismatch,
            result.Failure);
        Assert.Equal(new FanRpmReadings(3000, 3000), result.CandidateFanRpm);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x88));
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringFixedFanRampStillRestores()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new StatefulSession(
            "01010000",
            "01020000",
            firstFanRpm: 3000,
            secondFanRpm: 3000,
            afterCompletedFanSample: cancellation.Cancel);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            maximumSamples: 3,
            stateCheckEverySamples: 3,
            sampleInterval: TimeSpan.FromMilliseconds(1)).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                cancellation.Token);

        Assert.Equal(PerformanceFanValidationFailure.Canceled, result.Failure);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
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

    [Theory]
    [InlineData(PerformanceFanValidationTarget.Balanced, "01010000", "01020000")]
    [InlineData(PerformanceFanValidationTarget.BatterySaver, "01010300", "01020300")]
    [InlineData(PerformanceFanValidationTarget.BatteryBalanced, "01010600", "01020600")]
    public async Task RunAsync_AbortsWithoutWritesWhenTargetIsAlreadyActive(
        PerformanceFanValidationTarget target,
        string first,
        string second)
    {
        var session = new StatefulSession(first, second);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(target, default);

        Assert.Equal(PerformanceFanValidationFailure.TargetAlreadyActive, result.Failure);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x01 or 0x02);
    }

    [Theory]
    [InlineData("01010001", "01020001")]
    [InlineData("01010401", "01020401")]
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

    [Fact]
    public async Task RunAsync_RequiredPerformanceBaselineRejectsBalancedBeforeWrites()
    {
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            requiredBaselineState: ThermalModeState.PerformanceAutomatic).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.Equal(
            PerformanceFanValidationFailure.BaselineNotRequiredBaseMode,
            result.Failure);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(
            session.Commands,
            command => command.CommandId is 0x01 or 0x02);
    }

    [Fact]
    public async Task RunAsync_FixedFanTransitionsDirectlyFromPerformanceAndRestoresIt()
    {
        var session = new StatefulSession("01010200", "01020200");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            requiredBaselineState: ThermalModeState.PerformanceAutomatic).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.True(result.Success);
        string[] writes = session.Commands
            .Where(command => command.CommandId is 0x01 or 0x02)
            .Select(command => Convert.ToHexString(command.Arguments.Span))
            .ToArray();
        Assert.Equal(
            ["01010001", "01020001", "010136", "010236", "01010200", "01020200"],
            writes);
    }

    [Fact]
    public async Task RunAsync_RechecksSafetyBeforeEveryFixedApplyAndRestoreWrite()
    {
        var session = new StatefulSession("01010200", "01020200");
        var safetyGate = new ScriptedSafetyGate();

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            requiredBaselineState: ThermalModeState.PerformanceAutomatic,
            safetyGate: safetyGate,
            recheckSafetyBeforeEachWrite: true).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.True(result.Success);
        Assert.True(safetyGate.Calls >= 7);
    }

    [Fact]
    public async Task RunAsync_SafetyLossBeforeRestoreStopsWithoutARecoveryWrite()
    {
        var session = new StatefulSession("01010200", "01020200");
        var safetyGate = new ScriptedSafetyGate(failOnCall: 6);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            requiredBaselineState: ThermalModeState.PerformanceAutomatic,
            safetyGate: safetyGate,
            recheckSafetyBeforeEachWrite: true).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.Equal(PerformanceFanValidationFailure.SafetyAdmissionLost, result.Failure);
        Assert.True(result.ApplyAcknowledged);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.RestoreAttempted);
        Assert.False(result.RestoreAcknowledged);
        Assert.False(result.Restored);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(6, safetyGate.Calls);
        Assert.Equal(
            ["01010001", "01020001", "010136", "010236"],
            session.Commands
                .Where(command => command.CommandId is 0x01 or 0x02)
                .Select(command => Convert.ToHexString(command.Arguments.Span))
                .ToArray());
    }

    private static PerformanceFanSetterValidationRunner Runner(
        IRazerCommandSession session,
        int maximumSamples = 3,
        int stateCheckEverySamples = 3,
        TimeSpan? sampleInterval = null,
        ThermalModeState? requiredBaselineState = null,
        IPerformanceFanValidationSafetyGate? safetyGate = null,
        bool recheckSafetyBeforeEachWrite = false) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        new PendingDisposalRegistry(),
        fixedFanSampleInterval: sampleInterval ?? TimeSpan.Zero,
        fixedFanMaximumSamples: maximumSamples,
        fixedFanStateCheckEverySamples: stateCheckEverySamples,
        safetyGate: safetyGate,
        requiredBaselineState: requiredBaselineState,
        recheckSafetyBeforeEachWrite: recheckSafetyBeforeEachWrite);

    private sealed class ScriptedSafetyGate(int? failOnCall = null)
        : IPerformanceFanValidationSafetyGate
    {
        internal int Calls { get; private set; }

        public Task<PerformanceFanValidationSafetyGateResult> ValidateAsync(
            ValidationRunContext context,
            IRazerCommandSession session,
            CancellationToken cancellationToken)
        {
            Calls++;
            bool succeeds = Calls != failOnCall;
            return Task.FromResult(new PerformanceFanValidationSafetyGateResult(
                FullPowerAdapterConfirmed: succeeds,
                CoolingPadAbsentConfirmed: false,
                CoolingPadPresentConfirmed: false,
                CoolingPadOpaqueZeroConfirmed: false,
                succeeds
                    ? PerformanceFanValidationSafetyFailure.None
                    : PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired)
            {
                AdapterPowerState = succeeds
                    ? OpenBlade.Contracts.AdapterPowerState.FullPower330W
                    : OpenBlade.Contracts.AdapterPowerState.Battery,
                CoolingPadNotRequired = true,
            });
        }
    }

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
        int? failFanSelector = null,
        IReadOnlyList<FanRpmReadings>? fanRpmSamples = null,
        int? loseFixedStateAfterCompletedFanSamples = null,
        Action? afterCompletedFanSample = null) : IRazerCommandSession
    {
        private readonly byte[][] baseline =
            [Convert.FromHexString(firstHex), Convert.FromHexString(secondHex)];
        private readonly byte[][] state =
            [Convert.FromHexString(firstHex), Convert.FromHexString(secondHex)];
        private int completedFanSamples;
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

                FanRpmReadings sample = fanRpmSamples is { Count: > 0 }
                    ? fanRpmSamples[Math.Min(completedFanSamples, fanRpmSamples.Count - 1)]
                    : new(firstFanRpm, secondFanRpm);
                int rpm = selector == 1 ? sample.Selector1Rpm : sample.Selector2Rpm;
                byte[] response = [0x01, (byte)selector, checked((byte)(rpm / 100))];
                if (selector == 2)
                {
                    completedFanSamples++;
                    if (completedFanSamples == loseFixedStateAfterCompletedFanSamples)
                    {
                        state[0] = baseline[0].ToArray();
                        state[1] = baseline[1].ToArray();
                    }

                    afterCompletedFanSample?.Invoke();
                }

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

        public Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
