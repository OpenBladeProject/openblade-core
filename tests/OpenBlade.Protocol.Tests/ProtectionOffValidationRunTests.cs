using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProtectionOffValidationRunTests
{
    [Fact]
    public async Task HappyPath_UsesExactEightTransactionsOnOneActor()
    {
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Query(BatteryValidationSetting.ProtectionOff), Unit(),
            Query(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(Bool(false), Bool(false), Bool(false));
        var actorFactory = new ActorFactory(Actor(battery, temporary));
        IProtectionOffValidationPort port = (await Factory(actorFactory).OpenAsync(default)).Value!;

        Assert.Equal(BatteryValidationSetting.Limit80, (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
        Assert.Null((await port.ApplyProtectionOffAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80, (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(["Q1", "S3:ProtectionOff", "Q4", "S6:Limit80", "Q7"], battery.Events);
        Assert.Equal([2, 5, 8], temporary.TransactionIds);
        Assert.Equal(1, actorFactory.OpenCount);
    }

    [Fact]
    public async Task CandidateFailure_RetainsOnlyFixedLimit80RestorationPath()
    {
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Query(BatteryValidationSetting.ProtectionOff), Unit(),
            Query(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            Bool(false), Failure<bool>(HidSessionFailureKind.ProtocolFailure), Bool(false));
        IProtectionOffValidationPort port = (await Factory(
            new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;

        await port.QueryProtectionAsync(default);
        await port.QueryTemporaryFullChargeAsync(default);
        await port.ApplyProtectionOffAsync(default);
        await port.QueryProtectionAsync(default);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.QueryTemporaryFullChargeAsync(default)).Failure);
        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Contains("S6:Limit80", battery.Events);
        AssertFixedWrites(battery.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateSetterFailureOrThrow_RetainsExactlyOneFixedRestore(bool throws)
    {
        object candidate = throws
            ? new IOException("candidate setter failed")
            : Failure<HidUnit>(HidSessionFailureKind.NativeIoFailure);
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80), candidate, Unit(),
            Query(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(Bool(false), Bool(false));
        IProtectionOffValidationPort port = (await Factory(
            new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;
        await PrimeBaselineAsync(port);

        HidSessionResult<HidUnit> candidateResult = await port.ApplyProtectionOffAsync(default);
        Assert.NotNull(candidateResult.Failure);
        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(["Q1", "S3:ProtectionOff", "S6:Limit80", "Q7"], battery.Events);
        Assert.Equal([2, 8], temporary.TransactionIds);
        AssertFixedWrites(battery.Events);
    }

    [Fact]
    public async Task CandidateSetterCancellation_RetainsExactlyOneFixedRestore()
    {
        using var caller = new CancellationTokenSource();
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80),
            Failure<HidUnit>(HidSessionFailureKind.Canceled),
            Unit(),
            Query(BatteryValidationSetting.Limit80))
        {
            OnFirstSet = caller.Cancel,
        };
        var temporary = new TemporaryCommands(Bool(false), Bool(false));
        IProtectionOffValidationPort port = (await Factory(
            new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;
        await PrimeBaselineAsync(port);

        HidSessionResult<HidUnit> candidate =
            await port.ApplyProtectionOffAsync(caller.Token);
        Assert.Equal(HidSessionFailureKind.Canceled, candidate.Failure);
        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(["Q1", "S3:ProtectionOff", "S6:Limit80", "Q7"], battery.Events);
        Assert.Equal([2, 8], temporary.TransactionIds);
        AssertFixedWrites(battery.Events);
    }

    [Fact]
    public async Task CandidateProtectionReadFailure_StillUsesReservedRestoreIdsOnly()
    {
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure),
            Unit(), Query(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(Bool(false), Bool(false), Bool(false));
        IProtectionOffValidationPort port = (await Factory(
            new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;
        await PrimeBaselineAsync(port);

        Assert.Null((await port.ApplyProtectionOffAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.QueryProtectionAsync(default)).Failure);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(["Q1", "S3:ProtectionOff", "Q4", "S6:Limit80", "Q7"], battery.Events);
        Assert.Equal([2, 5, 8], temporary.TransactionIds);
        AssertFixedWrites(battery.Events);
    }

    [Fact]
    public async Task UnhealthyCandidateSession_ReopensSameContextAndUsesReservedRestoreIds()
    {
        var primarySession = new Session();
        var primaryBattery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80),
            Failure<HidUnit>(HidSessionFailureKind.Poisoned))
        {
            OnFirstSet = primarySession.RequireRecovery,
        };
        var primaryTemporary = new TemporaryCommands(Bool(false));
        var recoveryBattery = new BatteryCommands(Unit(), Query(BatteryValidationSetting.Limit80));
        var recoveryTemporary = new TemporaryCommands(Bool(false));
        var actors = new ActorFactory(
            Actor(primarySession, primaryBattery, primaryTemporary),
            Actor(new Session(), recoveryBattery, recoveryTemporary));
        IProtectionOffValidationPort port = (await Factory(actors).OpenAsync(default)).Value!;
        await PrimeBaselineAsync(port);

        Assert.Equal(HidSessionFailureKind.Poisoned,
            (await port.ApplyProtectionOffAsync(default)).Failure);
        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(2, actors.OpenCount);
        Assert.Same(actors.Contexts[0], actors.Contexts[1]);
        Assert.Equal(["Q1", "S3:ProtectionOff"], primaryBattery.Events);
        Assert.Equal(["S6:Limit80", "Q7"], recoveryBattery.Events);
        Assert.Equal([2], primaryTemporary.TransactionIds);
        Assert.Equal([8], recoveryTemporary.TransactionIds);
        AssertFixedWrites(primaryBattery.Events.Concat(recoveryBattery.Events));
    }

    [Fact]
    public async Task RestoreSetterFailure_IsAttemptedOnceAndTerminatesWithoutExtraIo()
    {
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Query(BatteryValidationSetting.ProtectionOff),
            Failure<HidUnit>(HidSessionFailureKind.NativeIoFailure));
        var temporary = new TemporaryCommands(Bool(false), Bool(false));
        IProtectionOffValidationPort port = (await Factory(
            new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;
        await RunCandidateReadbackAsync(port);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure,
            (await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(HidSessionFailureKind.Disposed,
            (await port.QueryProtectionAsync(default)).Failure);

        Assert.Equal(["Q1", "S3:ProtectionOff", "Q4", "S6:Limit80"], battery.Events);
        Assert.Equal([2, 5], temporary.TransactionIds);
        AssertFixedWrites(battery.Events);
    }

    [Fact]
    public async Task RestoreProtectionReadFailure_StillConsumesReservedTemporaryReadWithoutExtraWrite()
    {
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Query(BatteryValidationSetting.ProtectionOff), Unit(),
            Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure));
        var temporary = new TemporaryCommands(Bool(false), Bool(false), Bool(false));
        IProtectionOffValidationPort port = (await Factory(
            new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;
        await RunCandidateReadbackAsync(port);

        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.QueryProtectionAsync(default)).Failure);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);

        Assert.Equal(["Q1", "S3:ProtectionOff", "Q4", "S6:Limit80", "Q7"], battery.Events);
        Assert.Equal([2, 5, 8], temporary.TransactionIds);
        AssertFixedWrites(battery.Events);
    }

    [Fact]
    public async Task RestoreTemporaryReadFailure_TerminatesAfterReservedIdEightWithoutExtraWrite()
    {
        var battery = new BatteryCommands(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Query(BatteryValidationSetting.ProtectionOff), Unit(),
            Query(BatteryValidationSetting.Limit80));
        var temporary = new TemporaryCommands(
            Bool(false), Bool(false), Failure<bool>(HidSessionFailureKind.ProtocolFailure));
        IProtectionOffValidationPort port = (await Factory(
            new ActorFactory(Actor(battery, temporary))).OpenAsync(default)).Value!;
        await RunCandidateReadbackAsync(port);

        Assert.Null((await port.RestoreLimit80Async(default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.QueryTemporaryFullChargeAsync(default)).Failure);

        Assert.Equal(["Q1", "S3:ProtectionOff", "Q4", "S6:Limit80", "Q7"], battery.Events);
        Assert.Equal([2, 5, 8], temporary.TransactionIds);
        AssertFixedWrites(battery.Events);
    }

    private static ProtectionOffValidationRunFactory Factory(ActorFactory actors) =>
        new(() => new ValidationRunContext(), actors, TimeProvider.System,
            TimeSpan.FromSeconds(5), new PendingDisposalRegistry());

    private static async Task PrimeBaselineAsync(IProtectionOffValidationPort port)
    {
        Assert.Equal(BatteryValidationSetting.Limit80,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
    }

    private static async Task RunCandidateReadbackAsync(IProtectionOffValidationPort port)
    {
        await PrimeBaselineAsync(port);
        Assert.Null((await port.ApplyProtectionOffAsync(default)).Failure);
        Assert.Equal(BatteryValidationSetting.ProtectionOff,
            (await port.QueryProtectionAsync(default)).Value);
        Assert.False((await port.QueryTemporaryFullChargeAsync(default)).Value);
    }

    private static void AssertFixedWrites(IEnumerable<string> events)
    {
        string[] writes = events.Where(entry => entry.StartsWith("S", StringComparison.Ordinal)).ToArray();
        Assert.Equal(1, writes.Count(entry => entry == "S3:ProtectionOff"));
        Assert.Equal(1, writes.Count(entry => entry == "S6:Limit80"));
        Assert.Equal(2, writes.Length);
    }

    private static ValidationActor Actor(
        IBatteryCommandClient battery,
        ITemporaryFullChargeCommandClient temporary) => Actor(new Session(), battery, temporary);

    private static ValidationActor Actor(
        Session session,
        IBatteryCommandClient battery,
        ITemporaryFullChargeCommandClient temporary) => new(session, battery, temporary);

    private static HidSessionResult<BatteryValidationSetting> Query(BatteryValidationSetting value) => new(value, null);
    private static HidSessionResult<bool> Bool(bool value) => new(value, null);
    private static HidSessionResult<HidUnit> Unit() => new(new HidUnit(), null);
    private static HidSessionResult<T> Failure<T>(HidSessionFailureKind failure) => new(default, failure);

    private sealed class ActorFactory(params ValidationActor[] actors) : IValidationActorFactory
    {
        private readonly Queue<ValidationActor> actors = new(actors);
        internal int OpenCount { get; private set; }
        internal List<ValidationRunContext> Contexts { get; } = [];
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            OpenCount++;
            Contexts.Add(context);
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(actors.Dequeue(), null));
        }
    }

    private sealed class BatteryCommands(params object[] results) : IBatteryCommandClient
    {
        private readonly Queue<object> results = new(results);
        internal List<string> Events { get; } = [];
        internal Action? OnFirstSet { get; init; }
        private int setCount;
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte id, CancellationToken token)
        {
            Events.Add($"Q{id}");
            object next = results.Dequeue();
            return next is Exception error
                ? Task.FromException<HidSessionResult<BatteryValidationSetting>>(error)
                : Task.FromResult((HidSessionResult<BatteryValidationSetting>)next);
        }
        public Task<HidSessionResult<HidUnit>> SetAsync(byte id, BatteryValidationSetting setting, CancellationToken token)
        {
            Events.Add($"S{id}:{setting}");
            if (Interlocked.Increment(ref setCount) == 1)
            {
                OnFirstSet?.Invoke();
            }

            object next = results.Dequeue();
            return next is Exception error
                ? Task.FromException<HidSessionResult<HidUnit>>(error)
                : Task.FromResult((HidSessionResult<HidUnit>)next);
        }
    }

    private sealed class TemporaryCommands(params HidSessionResult<bool>[] results)
        : ITemporaryFullChargeCommandClient
    {
        private readonly Queue<HidSessionResult<bool>> results = new(results);
        internal List<byte> TransactionIds { get; } = [];
        public Task<HidSessionResult<bool>> QueryAsync(byte id, CancellationToken token)
        {
            TransactionIds.Add(id);
            return Task.FromResult(results.Dequeue());
        }

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte id,
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command, Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposition = RazerCommandSessionDisposition.FullyDisposed; return ValueTask.CompletedTask; }
        internal void RequireRecovery() => Disposition = RazerCommandSessionDisposition.RecoveryRequired;
    }
}
