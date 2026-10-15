using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class TemporaryFullChargeValidationRunTests
{
    [Fact]
    public async Task HappyPath_UsesExactTenTransactionsAndFixedWrites()
    {
        var battery = new BatteryCommands(
            Result(BatteryValidationSetting.Limit80),
            Result(BatteryValidationSetting.Limit80),
            Result(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            queries: [Result(false), Result(true), Result(false)],
            setters: [Unit(), Unit()],
            guards: [Reading(false, 0x00), Reading(true, 0x00)]);
        var actors = new ActorFactory(Actor(battery, temporary));
        ITemporaryFullChargeValidationPort port =
            (await Factory(actors).OpenAsync(default)).Value!;

        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
        Assert.Null((await port.ApplyTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.True((await port.QueryTemporaryFullChargeAsync(default)).Value);
        Assert.Null((await port.CancelTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(["Q1", "Q4", "Q7"], battery.Events);
        Assert.Equal(
            ["Q2", "G9", "S3:True", "Q5", "G10", "S6:False", "Q8"],
            temporary.Events);
        Assert.Equal(1, actors.OpenCount);
    }

    [Fact]
    public async Task FailedEnable_StillAllowsExactlyOneFixedCancelAndRestorationReadback()
    {
        var battery = new BatteryCommands(
            Result(BatteryValidationSetting.Limit80),
            Result(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            queries: [Result(false), Result(false)],
            setters: [Failure<HidUnit>(HidSessionFailureKind.NativeIoFailure), Unit()]);
        ITemporaryFullChargeValidationPort port =
            (await Factory(new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;

        await PrimeBaselineAsync(port);
        Assert.Equal(HidSessionFailureKind.NativeIoFailure,
            (await port.ApplyTemporaryFullChargeAsync(default)).Failure);
        Assert.Null((await port.CancelTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
        Assert.Equal(HidSessionFailureKind.Disposed,
            (await port.CancelTemporaryFullChargeAsync(default)).Failure);

        Assert.Equal(["Q2", "G9", "S3:True", "G10", "S6:False", "Q8"], temporary.Events);
        Assert.Equal(["Q1", "Q7"], battery.Events);
    }

    [Fact]
    public async Task CandidateReadFailure_DoesNotRemoveFixedCancelPath()
    {
        var battery = new BatteryCommands(
            Result(BatteryValidationSetting.Limit80),
            Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure),
            Result(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            queries: [Result(false), Result(true), Result(false)],
            setters: [Unit(), Unit()]);
        ITemporaryFullChargeValidationPort port =
            (await Factory(new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;

        await PrimeBaselineAsync(port);
        Assert.Null((await port.ApplyTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.QueryProtectionAsync(default)).Failure);
        Assert.True((await port.QueryTemporaryFullChargeAsync(default)).Value);
        Assert.Null((await port.CancelTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(["S3:True", "S6:False"],
            temporary.Events.Where(item => item.StartsWith('S')).ToArray());
    }

    [Fact]
    public async Task RecoveryRequiredAfterEnable_ReopensSameContextOnlyForFixedCancel()
    {
        var primarySession = new Session();
        var primaryBattery = new BatteryCommands(Result(BatteryValidationSetting.Limit80));
        var primaryTemporary = new TemporaryCommands(
            queries: [Result(false)],
            setters: [Failure<HidUnit>(HidSessionFailureKind.Poisoned)])
        {
            OnFirstSet = primarySession.RequireRecovery,
        };
        var recoveryBattery = new BatteryCommands(Result(BatteryValidationSetting.Limit80));
        var recoveryTemporary = new TemporaryCommands(
            queries: [Result(false)],
            setters: [Unit()]);
        var actors = new ActorFactory(
            Actor(primarySession, primaryBattery, primaryTemporary),
            Actor(new Session(), recoveryBattery, recoveryTemporary));
        ITemporaryFullChargeValidationPort port =
            (await Factory(actors).OpenAsync(default)).Value!;

        await PrimeBaselineAsync(port);
        Assert.Equal(HidSessionFailureKind.Poisoned,
            (await port.ApplyTemporaryFullChargeAsync(default)).Failure);
        Assert.Null((await port.CancelTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(2, actors.OpenCount);
        Assert.Same(actors.Contexts[0], actors.Contexts[1]);
        Assert.Equal(["S3:True"], primaryTemporary.Events.Where(IsSetter).ToArray());
        Assert.Equal(["S6:False"], recoveryTemporary.Events.Where(IsSetter).ToArray());
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    public async Task OtherPowerModeSettingsOn_SendsNeitherFixedWrite(byte otherSettings)
    {
        var battery = new BatteryCommands(Result(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            queries: [Result(false)],
            setters: [],
            guards: [Reading(false, otherSettings), Reading(false, otherSettings)]);
        ITemporaryFullChargeValidationPort port =
            (await Factory(new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;

        await PrimeBaselineAsync(port);
        HidSessionResult<HidUnit> enable = await port.ApplyTemporaryFullChargeAsync(default);
        HidSessionResult<HidUnit> cancel = await port.CancelTemporaryFullChargeAsync(default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, enable.Failure);
        Assert.Equal(HidSessionFailurePhase.Guard, enable.Diagnostic?.Phase);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, cancel.Failure);
        Assert.Equal(HidSessionFailureKind.Disposed,
            (await port.QueryProtectionAsync(default)).Failure);
        Assert.Equal(["Q2", "G9", "G10"], temporary.Events);
    }

    [Fact]
    public async Task CancelGuard_BlocksWhenLocalDimmingTurnsOnAfterEnable()
    {
        var battery = new BatteryCommands(
            Result(BatteryValidationSetting.Limit80),
            Result(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            queries: [Result(false), Result(true)],
            setters: [Unit()],
            guards: [Reading(false, 0x00), Reading(true, 0x08)]);
        ITemporaryFullChargeValidationPort port =
            (await Factory(new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;

        await PrimeBaselineAsync(port);
        Assert.Null((await port.ApplyTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.True((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.CancelTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(["Q2", "G9", "S3:True", "Q5", "G10"], temporary.Events);
    }

    [Fact]
    public async Task GuardReadFailure_SendsNoEnable()
    {
        var battery = new BatteryCommands(Result(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            queries: [Result(false)],
            setters: [],
            guards:
            [
                Failure<TemporaryFullChargeReading>(HidSessionFailureKind.ProtocolFailure),
                Failure<TemporaryFullChargeReading>(HidSessionFailureKind.ProtocolFailure),
            ]);
        ITemporaryFullChargeValidationPort port =
            (await Factory(new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;

        await PrimeBaselineAsync(port);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyTemporaryFullChargeAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.CancelTemporaryFullChargeAsync(default)).Failure);
        Assert.DoesNotContain(temporary.Events, IsSetter);
    }

    private static bool IsSetter(string value) => value.StartsWith('S');

    private static TemporaryFullChargeValidationRunFactory Factory(ActorFactory actors) =>
        new(
            static () => new ValidationRunContext(),
            actors,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry());

    private static async Task PrimeBaselineAsync(ITemporaryFullChargeValidationPort port)
    {
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
    }

    private static ValidationActor Actor(
        BatteryCommands battery,
        TemporaryCommands temporary) => Actor(new Session(), battery, temporary);

    private static ValidationActor Actor(
        Session session,
        BatteryCommands battery,
        TemporaryCommands temporary) => new(session, battery, temporary);

    private static HidSessionResult<T> Result<T>(T value) => new(value, null);

    private static HidSessionResult<T> Failure<T>(HidSessionFailureKind failure) =>
        new(default, failure);

    private static HidSessionResult<HidUnit> Unit() => new(new HidUnit(), null);

    private static HidSessionResult<TemporaryFullChargeReading> Reading(
        bool active,
        byte otherSettings) => new(new TemporaryFullChargeReading(active, otherSettings), null);

    private sealed class ActorFactory(params ValidationActor[] actors) : IValidationActorFactory
    {
        private readonly Queue<ValidationActor> actors = new(actors);
        internal int OpenCount { get; private set; }
        internal List<ValidationRunContext> Contexts { get; } = [];

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            OpenCount++;
            Contexts.Add(context);
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(
                actors.Dequeue(),
                null));
        }
    }

    private sealed class BatteryCommands(params HidSessionResult<BatteryValidationSetting>[] queries)
        : IBatteryCommandClient
    {
        private readonly Queue<HidSessionResult<BatteryValidationSetting>> queries = new(queries);
        internal List<string> Events { get; } = [];

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken token)
        {
            Events.Add($"Q{transactionId}");
            return Task.FromResult(queries.Dequeue());
        }

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class TemporaryCommands(
        IEnumerable<HidSessionResult<bool>> queries,
        IEnumerable<HidSessionResult<HidUnit>> setters,
        IEnumerable<HidSessionResult<TemporaryFullChargeReading>>? guards = null)
        : ITemporaryFullChargeCommandClient
    {
        private readonly Queue<HidSessionResult<TemporaryFullChargeReading>> guards =
            new(guards ?? []);
        private readonly Queue<HidSessionResult<bool>> queries = new(queries);
        private readonly Queue<HidSessionResult<HidUnit>> setters = new(setters);
        private int setCount;
        internal Action? OnFirstSet { get; init; }
        internal List<string> Events { get; } = [];

        public Task<HidSessionResult<bool>> QueryAsync(byte id, CancellationToken token)
        {
            Events.Add($"Q{id}");
            return Task.FromResult(queries.Dequeue());
        }

        public Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
            byte id,
            CancellationToken token)
        {
            Events.Add($"G{id}");
            return Task.FromResult(guards.Count > 0
                ? guards.Dequeue()
                : new HidSessionResult<TemporaryFullChargeReading>(
                    new TemporaryFullChargeReading(false, 0), null));
        }

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte id,
            bool enabled,
            CancellationToken token)
        {
            Events.Add($"S{id}:{enabled}");
            if (setCount++ == 0)
            {
                OnFirstSet?.Invoke();
            }

            return Task.FromResult(setters.Dequeue());
        }
    }

    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assign,
            TimeSpan timeout,
            CancellationToken token) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        internal void RequireRecovery() =>
            Disposition = RazerCommandSessionDisposition.RecoveryRequired;
    }
}
