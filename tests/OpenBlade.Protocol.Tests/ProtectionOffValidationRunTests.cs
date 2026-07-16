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
        Assert.DoesNotContain(battery.Events, entry => entry.StartsWith("S", StringComparison.Ordinal) &&
            entry is not "S3:ProtectionOff" and not "S6:Limit80");
    }

    private static ProtectionOffValidationRunFactory Factory(ActorFactory actors) =>
        new(() => new ValidationRunContext(), actors, TimeProvider.System,
            TimeSpan.FromSeconds(5), new PendingDisposalRegistry());

    private static ValidationActor Actor(
        IBatteryCommandClient battery,
        ITemporaryFullChargeCommandClient temporary) => new(new Session(), battery, temporary);

    private static HidSessionResult<BatteryValidationSetting> Query(BatteryValidationSetting value) => new(value, null);
    private static HidSessionResult<bool> Bool(bool value) => new(value, null);
    private static HidSessionResult<HidUnit> Unit() => new(new HidUnit(), null);
    private static HidSessionResult<T> Failure<T>(HidSessionFailureKind failure) => new(default, failure);

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        internal int OpenCount { get; private set; }
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            OpenCount++;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
        }
    }

    private sealed class BatteryCommands(params object[] results) : IBatteryCommandClient
    {
        private readonly Queue<object> results = new(results);
        internal List<string> Events { get; } = [];
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte id, CancellationToken token)
        {
            Events.Add($"Q{id}");
            return Task.FromResult((HidSessionResult<BatteryValidationSetting>)results.Dequeue());
        }
        public Task<HidSessionResult<HidUnit>> SetAsync(byte id, BatteryValidationSetting setting, CancellationToken token)
        {
            Events.Add($"S{id}:{setting}");
            return Task.FromResult((HidSessionResult<HidUnit>)results.Dequeue());
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
    }

    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command, Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposition = RazerCommandSessionDisposition.FullyDisposed; return ValueTask.CompletedTask; }
    }
}
