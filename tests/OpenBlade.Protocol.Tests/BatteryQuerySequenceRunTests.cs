using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatteryQuerySequenceRunTests
{
    private static readonly TimeSpan Drain = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task QueryAsync_UsesOneActorForExactlyFiveQueriesAndNeverSets()
    {
        var commands = new ScriptedBatteryCommands(
            Success(), Success(), Success(), Success(), Success());
        var session = new ScriptedSession();
        var actors = new ScriptedActorFactory(new ValidationActor(session, commands));
        var time = new ImmediateTimeProvider();
        IBatteryQuerySequencePort port = (await Factory(
            new ValidationRunContext(), actors, time).OpenAsync(default)).Value!;

        byte[] expectedIds = [1, 2, 3, 4, 5];
        foreach (byte expectedId in expectedIds)
        {
            HidSessionResult<BatteryValidationSetting> result =
                await port.QueryAsync(CancellationToken.None);
            Assert.Null(result.Failure);
            Assert.Equal(BatteryValidationSetting.ProtectionOff, result.Value);
        }

        Assert.Equal(expectedIds, commands.QueryTransactionIds);
        Assert.Equal(0, commands.SetCount);
        Assert.Equal(1, actors.OpenCount);
        Assert.Empty(time.DueTimes);

        Assert.Equal(
            HidSessionFailureKind.Disposed,
            (await port.QueryAsync(default)).Failure);
        Assert.Equal(1, session.DisposeCount);
        await port.DisposeAsync();
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task QueryAsync_FailureTerminatesRunAndDisposesExactlyOnce()
    {
        var commands = new ScriptedBatteryCommands(
            Success(), Failure(HidSessionFailureKind.Poisoned), Success());
        var session = new ScriptedSession();
        var actors = new ScriptedActorFactory(new ValidationActor(session, commands));
        IBatteryQuerySequencePort port = (await Factory(
            new ValidationRunContext(), actors, new ImmediateTimeProvider()).OpenAsync(default)).Value!;

        Assert.Null((await port.QueryAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, (await port.QueryAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.Disposed, (await port.QueryAsync(default)).Failure);
        Assert.Equal(new byte[] { 1, 2 }, commands.QueryTransactionIds);
        Assert.Equal(1, session.DisposeCount);

        await port.DisposeAsync();
        await port.DisposeAsync();
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task QueryAsync_PreCanceledCallPerformsNoQueryAndTerminatesRun()
    {
        var commands = new ScriptedBatteryCommands(Success());
        var session = new ScriptedSession();
        var actors = new ScriptedActorFactory(new ValidationActor(session, commands));
        IBatteryQuerySequencePort port = (await Factory(
            new ValidationRunContext(), actors, new ImmediateTimeProvider()).OpenAsync(default)).Value!;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Equal(HidSessionFailureKind.Canceled, (await port.QueryAsync(canceled.Token)).Failure);
        Assert.Equal(HidSessionFailureKind.Disposed, (await port.QueryAsync(default)).Failure);
        Assert.Empty(commands.QueryTransactionIds);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task QueryAsync_CanceledWaiterCannotBeOvertakenByLaterQueuedQuery()
    {
        var commands = new BlockingFirstQueryCommands();
        var session = new ScriptedSession();
        var actors = new ScriptedActorFactory(new ValidationActor(session, commands));
        IBatteryQuerySequencePort port = (await Factory(
            new ValidationRunContext(), actors, new ImmediateTimeProvider()).OpenAsync(default)).Value!;

        Task<HidSessionResult<BatteryValidationSetting>> first = port.QueryAsync(default);
        await commands.FirstQueryEntered.Task;
        using var canceled = new CancellationTokenSource();
        Task<HidSessionResult<BatteryValidationSetting>> second = port.QueryAsync(canceled.Token);
        Task<HidSessionResult<BatteryValidationSetting>> third = port.QueryAsync(default);
        canceled.Cancel();
        commands.ReleaseFirstQuery.TrySetResult();

        Assert.Null((await first).Failure);
        Assert.Equal(HidSessionFailureKind.Canceled, (await second).Failure);
        Assert.Equal(HidSessionFailureKind.Disposed, (await third).Failure);
        Assert.Equal(new byte[] { 1 }, commands.QueryTransactionIds);
        Assert.Equal(1, session.DisposeCount);
        await port.DisposeAsync();
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void TryReserveBatteryQuerySequence_ReturnsFreshAtomicFiveIdReservations()
    {
        var context = new ValidationRunContext();

        Assert.True(context.TryReserveBatteryQuerySequence(out byte[] first));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, first);
        first[0] = 99;
        Assert.True(context.TryReserveBatteryQuerySequence(out byte[] second));
        Assert.Equal(new byte[] { 6, 7, 8, 9, 10 }, second);
    }

    [Theory]
    [InlineData(251)]
    [InlineData(252)]
    [InlineData(253)]
    [InlineData(254)]
    public void TryReserveBatteryQuerySequence_WithOneToFourIdsRemainingFailsWithoutAdvancing(
        int consumedCount)
    {
        var context = new ValidationRunContext();
        for (int index = 0; index < consumedCount; index++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }

        Assert.False(context.TryReserveBatteryQuerySequence(out byte[] unavailable));
        Assert.Empty(unavailable);
        Assert.True(context.TryAllocateTransaction(out byte unchangedNext));
        Assert.Equal(consumedCount + 1, unchangedNext);
    }

    private static BatteryQuerySequenceRunFactory Factory(
        ValidationRunContext context,
        ScriptedActorFactory actors,
        TimeProvider time) =>
        new(() => context, actors, time, Drain, new PendingDisposalRegistry());

    private static HidSessionResult<BatteryValidationSetting> Success() =>
        new(BatteryValidationSetting.ProtectionOff, null);

    private static HidSessionResult<BatteryValidationSetting> Failure(
        HidSessionFailureKind failure) => new(default, failure);

    private sealed class ScriptedActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        internal int OpenCount { get; private set; }

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            OpenCount++;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
        }
    }

    private sealed class ScriptedBatteryCommands(
        params HidSessionResult<BatteryValidationSetting>[] results) : IBatteryCommandClient
    {
        private readonly Queue<HidSessionResult<BatteryValidationSetting>> results = new(results);
        internal List<byte> QueryTransactionIds { get; } = [];
        internal int SetCount { get; private set; }

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken)
        {
            QueryTransactionIds.Add(transactionId);
            return Task.FromResult(results.Dequeue());
        }

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken)
        {
            SetCount++;
            throw new InvalidOperationException("Query sequence must never set battery state.");
        }
    }

    private sealed class BlockingFirstQueryCommands : IBatteryCommandClient
    {
        internal TaskCompletionSource FirstQueryEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstQuery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<byte> QueryTransactionIds { get; } = [];

        public async Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken)
        {
            QueryTransactionIds.Add(transactionId);
            if (QueryTransactionIds.Count == 1)
            {
                FirstQueryEntered.TrySetResult();
                await ReleaseFirstQuery.Task;
            }

            return Success();
        }

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Query sequence must never set battery state.");
    }

    private sealed class ScriptedSession : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;
        internal int DisposeCount { get; private set; }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        internal List<TimeSpan> DueTimes { get; } = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            DueTimes.Add(dueTime);
            callback(state);
            return new CompletedTimer();
        }
    }

    private sealed class CompletedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
