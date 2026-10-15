using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class DirectBatteryTransitionValidationRunnerTests
{
    [Fact]
    public async Task RunAsync_AppliesDirectOffAndRestoresExactBaseline()
    {
        var battery = new StatefulBatteryCommands();
        var safety = new ScriptedSafetyGate();
        var session = new DisposableSession();

        DirectBatteryTransitionValidationResult result = await Runner(
            battery,
            safety,
            session).RunAsync(default);

        Assert.True(result.Success);
        Assert.Equal(State(BatteryValidationSetting.Limit80), result.Baseline);
        Assert.Equal(State(BatteryValidationSetting.Limit70), result.PreOff);
        Assert.Equal(State(BatteryValidationSetting.ProtectionOff), result.Candidate);
        Assert.Equal(State(BatteryValidationSetting.Limit80), result.Restored);
        Assert.Equal(
            [
                DirectBatteryTransitionValidationStep.BaselineFirstRead,
                DirectBatteryTransitionValidationStep.BaselineSecondRead,
                DirectBatteryTransitionValidationStep.Limit70SafetyAdmission,
                DirectBatteryTransitionValidationStep.Limit70Acknowledged,
                DirectBatteryTransitionValidationStep.PreOffRead,
                DirectBatteryTransitionValidationStep.ProtectionOffSafetyAdmission,
                DirectBatteryTransitionValidationStep.ProtectionOffAcknowledged,
                DirectBatteryTransitionValidationStep.CandidateRead,
                DirectBatteryTransitionValidationStep.RestorationSafetyAdmission,
                DirectBatteryTransitionValidationStep.RestorationAcknowledged,
                DirectBatteryTransitionValidationStep.RestorationRead,
            ],
            result.Steps);
        Assert.Equal(
            [
                BatteryValidationSetting.Limit70,
                BatteryValidationSetting.ProtectionOff,
                BatteryValidationSetting.Limit80,
            ],
            battery.SetterCalls);
        Assert.Equal(3, safety.Calls);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RunAsync_AmbiguousCandidateStopsWithoutRestorationWrite()
    {
        var battery = new StatefulBatteryCommands(failQueryNumber: 4);
        var safety = new ScriptedSafetyGate();
        var session = new DisposableSession();

        DirectBatteryTransitionValidationResult result = await Runner(
            battery,
            safety,
            session).RunAsync(default);

        Assert.Equal(
            DirectBatteryTransitionValidationFailure.CandidateReadFailed,
            result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Null(result.Candidate);
        Assert.Null(result.Restored);
        Assert.Equal(
            [
                BatteryValidationSetting.Limit70,
                BatteryValidationSetting.ProtectionOff,
            ],
            battery.SetterCalls);
        Assert.Equal(2, safety.Calls);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RunAsync_SafetyAdmissionLostBeforeRestoreStopsWithoutWrite()
    {
        var battery = new StatefulBatteryCommands();
        var safety = new ScriptedSafetyGate(failOnCall: 3);
        var session = new DisposableSession();

        DirectBatteryTransitionValidationResult result = await Runner(
            battery,
            safety,
            session).RunAsync(default);

        Assert.Equal(
            DirectBatteryTransitionValidationFailure.SafetyAdmissionLost,
            result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(State(BatteryValidationSetting.ProtectionOff), result.Candidate);
        Assert.Null(result.Restored);
        Assert.Equal(
            [
                BatteryValidationSetting.Limit70,
                BatteryValidationSetting.ProtectionOff,
            ],
            battery.SetterCalls);
        Assert.Equal(3, safety.Calls);
        Assert.True(session.Disposed);
    }

    private static DirectBatteryTransitionState State(BatteryValidationSetting setting) =>
        new(setting, TemporaryFullCharge: false);

    private static DirectBatteryTransitionValidationRunner Runner(
        StatefulBatteryCommands battery,
        IPerformanceFanValidationSafetyGate safety,
        DisposableSession session) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            battery,
            new InactiveTemporaryFullChargeCommands())),
        safety,
        TimeProvider.System,
        TimeSpan.FromSeconds(1),
        new PendingDisposalRegistry());

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class StatefulBatteryCommands(int? failQueryNumber = null)
        : IBatteryCommandClient
    {
        private BatteryValidationSetting current = BatteryValidationSetting.Limit80;
        private int queryCount;

        internal List<BatteryValidationSetting> SetterCalls { get; } = [];

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken)
        {
            queryCount++;
            return queryCount == failQueryNumber
                ? Task.FromResult(new HidSessionResult<BatteryValidationSetting>(
                    default,
                    HidSessionFailureKind.Timeout))
                : Task.FromResult(new HidSessionResult<BatteryValidationSetting>(
                    current,
                    null));
        }

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken)
        {
            SetterCalls.Add(setting);
            current = setting;
            return Task.FromResult(new HidSessionResult<HidUnit>(new HidUnit(), null));
        }
    }

    private sealed class InactiveTemporaryFullChargeCommands
        : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
            byte transactionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HidSessionResult<TemporaryFullChargeReading>(
                new TemporaryFullChargeReading(false, 0), null));

        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HidSessionResult<bool>(false, null));

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

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
            bool success = Calls != failOnCall;
            return Task.FromResult(new PerformanceFanValidationSafetyGateResult(
                FullPowerAdapterConfirmed: success,
                CoolingPadAbsentConfirmed: false,
                CoolingPadPresentConfirmed: false,
                CoolingPadOpaqueZeroConfirmed: false,
                success
                    ? PerformanceFanValidationSafetyFailure.None
                    : PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired)
            {
                AdapterPowerState = success
                    ? OpenBlade.Contracts.AdapterPowerState.FullPower330W
                    : OpenBlade.Contracts.AdapterPowerState.Battery,
                CoolingPadNotRequired = true,
            });
        }
    }

    private sealed class DisposableSession : IRazerCommandSession
    {
        internal bool Disposed { get; private set; }

        public RazerCommandSessionDisposition Disposition => Disposed
            ? RazerCommandSessionDisposition.FullyDisposed
            : RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
