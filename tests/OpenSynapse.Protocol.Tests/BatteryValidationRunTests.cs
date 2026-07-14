using System.Collections.Concurrent;
using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class BatteryValidationRunTests
{
    private static readonly TimeSpan Drain = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OpenAsync_AlreadyCanceled_DoesNotCreateContextOrOpenActor()
    {
        int contexts = 0;
        var actors = new ScriptedActorFactory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new BatteryValidationRunFactory(
            () => { contexts++; return new ValidationRunContext(); }, actors, TimeProvider.System, Drain);

        HidSessionResult<IBatteryValidationPort> result = await factory.OpenAsync(cancellation.Token);

        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Equal(0, contexts);
        Assert.Equal(0, actors.OpenCount);
    }

    [Fact]
    public async Task OpenAsync_ReservationExhausted_DoesNotOpenActor()
    {
        var context = new ValidationRunContext();
        Consume(context, 251);
        var actors = new ScriptedActorFactory();
        var factory = Factory(context, actors);

        HidSessionResult<IBatteryValidationPort> result = await factory.OpenAsync(default);

        Assert.Equal(HidSessionFailureKind.Exhausted, result.Failure);
        Assert.Equal(0, actors.OpenCount);
    }

    [Fact]
    public async Task OpenAsync_CancellationAfterActorOpen_DisposesActorAndReturnsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new ScriptedSession();
        var actors = new ScriptedActorFactory(Actor(session, new ScriptedClient()))
        {
            OnOpen = cancellation.Cancel,
        };

        HidSessionResult<IBatteryValidationPort> result = await
            Factory(new ValidationRunContext(), actors).OpenAsync(cancellation.Token);

        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Null(result.Value);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task OpenAsync_MixedFailureAndActor_DisposesActorAndPreservesFailure()
    {
        var session = new ScriptedSession();
        var actors = new ScriptedActorFactory(new HidSessionResult<ValidationActor>(
            Actor(session, new ScriptedClient()), HidSessionFailureKind.ConflictBlocked));

        HidSessionResult<IBatteryValidationPort> result = await
            Factory(new ValidationRunContext(), actors).OpenAsync(default);

        Assert.Equal(HidSessionFailureKind.ConflictBlocked, result.Failure);
        Assert.Null(result.Value);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task OpenAsync_MixedFailureRetainsTimedOutActorUntilCompletion()
    {
        var time = new ManualTimeProvider();
        var registry = new PendingDisposalRegistry();
        var session = new ScriptedSession(pendingDispose: true);
        var actors = new ScriptedActorFactory(new HidSessionResult<ValidationActor>(
            Actor(session, new ScriptedClient()), HidSessionFailureKind.IdentityBlocked));

        Task<HidSessionResult<IBatteryValidationPort>> opening =
            Factory(new ValidationRunContext(), actors, time, registry).OpenAsync(default);
        time.Advance(Drain);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, (await opening).Failure);
        Assert.Equal(1, registry.Count);

        session.CompleteDispose();
        await registry.WhenEmptyAsync();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task HappyPath_UsesFiveReservedIdsOnOneActor()
    {
        var context = new ValidationRunContext();
        Consume(context, 250);
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(), Query(BatteryValidationSetting.Limit70),
            Unit(), Query(BatteryValidationSetting.Limit80));
        var session = new ScriptedSession();
        var actors = new ScriptedActorFactory(Actor(session, client));
        IBatteryValidationPort port = (await Factory(context, actors).OpenAsync(default)).Value!;

        Assert.Null((await port.QueryAsync(default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Null((await port.QueryAsync(default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Null((await port.QueryAsync(default)).Failure);

        Assert.Equal(["Q251", "S252:Limit70", "Q253", "S254:Limit80", "Q255"], client.Events);
        Assert.Equal(1, session.DisposeCount);
        Assert.False(context.TryAllocateTransaction(out _));
    }

    [Fact]
    public async Task CandidateMustBeDefinedDifferentAndOutOfOrderCallsDoNoIo()
    {
        var client = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Unit(), Unit());
        var session = new ScriptedSession();
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(session, client))).OpenAsync(default)).Value!;

        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Null((await port.QueryAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyAsync((BatteryValidationSetting)999, default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70"], client.Events);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateQueryWhileAwaitingCandidateApply_DoesNoIoAndCandidateStillUsesId2()
    {
        var client = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Unit());
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        string[] before = [.. client.Events];

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await port.QueryAsync(default)).Failure);
        Assert.Equal(before, client.Events);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70"], client.Events);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task WrongApplyWhileAwaitingCandidateQuery_DoesNoIoAndQueryStillUsesId3()
    {
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(), Query(BatteryValidationSetting.Limit70));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);
        string[] before = [.. client.Events];

        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit65, default)).Failure);
        Assert.Equal(before, client.Events);
        Assert.Null((await port.QueryAsync(default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "Q3"], client.Events);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task QueryWhileCleanupEligible_DoesNoIoAndRestoreStillUsesId4()
    {
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Query(BatteryValidationSetting.Limit70), Unit());
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);
        await port.QueryAsync(default);
        string[] before = [.. client.Events];

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await port.QueryAsync(default)).Failure);
        Assert.Equal(before, client.Events);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "Q3", "S4:Limit80"], client.Events);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateRestoreApplyWhileAwaitingRestoreQuery_DoesNoIoAndQueryStillUsesId5()
    {
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(), Unit(),
            Query(BatteryValidationSetting.Limit80));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);
        await port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        string[] before = [.. client.Events];

        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(before, client.Events);
        Assert.Null((await port.QueryAsync(default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "S4:Limit80", "Q5"], client.Events);
    }

    [Fact]
    public async Task DirectRestore_BurnsCandidateQueryId()
    {
        var client = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Unit(), Unit(), Query(BatteryValidationSetting.Limit80));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Null((await port.QueryAsync(default)).Failure);

        Assert.Equal(["Q1", "S2:Limit70", "S4:Limit80", "Q5"], client.Events);
    }

    [Fact]
    public async Task CandidateFailure_RemainsCleanupEligible()
    {
        var client = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Canceled), Unit());
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);

        Assert.Equal(HidSessionFailureKind.Canceled,
            (await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "S4:Limit80"], client.Events);
    }

    [Fact]
    public async Task BaselineCommandException_TerminatesAndDisposes()
    {
        var session = new ScriptedSession();
        var client = new ScriptedClient(new InvalidOperationException("baseline"));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(session, client))).OpenAsync(default)).Value!;

        Assert.Equal(HidSessionFailureKind.NativeIoFailure, (await port.QueryAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.Disposed, (await port.QueryAsync(default)).Failure);
        Assert.Equal(["Q1"], client.Events);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task CandidateCommandException_BurnsStageAndStillAllowsRestore()
    {
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), new InvalidOperationException("candidate"), Unit());
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "S4:Limit80"], client.Events);
    }

    [Fact]
    public async Task RestoreCommandException_TerminatesWithoutReusableRestoreId()
    {
        var session = new ScriptedSession();
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(), new InvalidOperationException("restore"));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(session, client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(HidSessionFailureKind.Disposed,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "S4:Limit80"], client.Events);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task CandidateQueryException_BurnsQueryAndStillAllowsRestore()
    {
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(),
            new InvalidOperationException("candidate query"), Unit());
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure, (await port.QueryAsync(default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "Q3", "S4:Limit80"], client.Events);
    }

    [Fact]
    public async Task RestoreQueryException_TerminatesWithoutReusableQueryId()
    {
        var session = new ScriptedSession();
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(), Unit(),
            new InvalidOperationException("restore query"));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(session, client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);
        await port.ApplyAsync(BatteryValidationSetting.Limit80, default);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure, (await port.QueryAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.Disposed, (await port.QueryAsync(default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "S4:Limit80", "Q5"], client.Events);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Recovery_WaitsForFullDisposalAndUsesSameContextAndReservedIds()
    {
        var primaryClient = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned));
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        var recoveryClient = new ScriptedClient(Unit(), Query(BatteryValidationSetting.Limit80));
        var recovery = new ScriptedSession();
        var actors = new ScriptedActorFactory(Actor(primary, primaryClient), Actor(recovery, recoveryClient));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        Assert.Equal(1, actors.OpenCount);
        primary.CompleteDispose();
        Assert.Null((await restore).Failure);
        Assert.Null((await port.QueryAsync(default)).Failure);

        Assert.Equal(2, actors.OpenCount);
        Assert.Same(actors.Contexts[0], actors.Contexts[1]);
        Assert.Equal(["S4:Limit80", "Q5"], recoveryClient.Events);
    }

    [Fact]
    public async Task FaultedDisposalWithFullyDisposedDisposition_PermitsRecovery()
    {
        var primaryClient = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned));
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        var recoveryClient = new ScriptedClient(Unit());
        var actors = new ScriptedActorFactory(
            Actor(primary, primaryClient), Actor(new ScriptedSession(), recoveryClient));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        primary.FailDispose(new InvalidOperationException("observed disposal fault"));

        Assert.Null((await restore).Failure);
        Assert.Equal(2, actors.OpenCount);
        Assert.Equal(["S4:Limit80"], recoveryClient.Events);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task Recovery_UsesReserved254And255WhenOrdinaryAllocatorIsExhausted()
    {
        var context = new ValidationRunContext();
        Consume(context, 250);
        var recoveryClient = new ScriptedClient(Unit(), Query(BatteryValidationSetting.Limit80));
        var actors = new ScriptedActorFactory(
            Actor(new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired),
                new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned))),
            Actor(new ScriptedSession(), recoveryClient));
        IBatteryValidationPort port = (await Factory(context, actors).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Null((await port.QueryAsync(default)).Failure);

        Assert.Equal(["S254:Limit80", "Q255"], recoveryClient.Events);
        Assert.False(context.TryAllocateTransaction(out _));
        Assert.Equal(2, actors.OpenCount);
        Assert.Same(actors.Contexts[0], actors.Contexts[1]);
    }

    [Fact]
    public async Task RecoveryDrainTimeout_NeverOpensRecoveryAndLaterDisposeIsBounded()
    {
        var time = new ManualTimeProvider();
        var primaryClient = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned));
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        var actors = new ScriptedActorFactory(Actor(primary, primaryClient), Actor(new ScriptedSession(), new ScriptedClient()));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors, time).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        time.Advance(Drain);

        Assert.Equal(HidSessionFailureKind.Timeout, (await restore).Failure);
        Assert.Equal(1, actors.OpenCount);
        await port.DisposeAsync();
        primary.FailDispose(new InvalidOperationException("late"));
    }

    [Fact]
    public async Task TimedOutDisposal_IsProcessRootedUntilLateCompletion()
    {
        var time = new ManualTimeProvider();
        var registry = new PendingDisposalRegistry();
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        var actors = new ScriptedActorFactory(Actor(primary,
            new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned))));
        IBatteryValidationPort port = (await Factory(
            new ValidationRunContext(), actors, time, registry).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        time.Advance(Drain);
        Assert.Equal(HidSessionFailureKind.Timeout, (await restore).Failure);
        Assert.Equal(1, registry.Count);

        primary.FailDispose(new InvalidOperationException("late observed fault"));
        await registry.WhenEmptyAsync();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DeadlineWinner_RemainsWinnerWhenDisposalCompletesBeforeAwaitResumes()
    {
        var time = new ManualTimeProvider();
        var primaryClient = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned));
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        var actors = new ScriptedActorFactory(
            Actor(primary, primaryClient),
            Actor(new ScriptedSession(), new ScriptedClient(Unit())));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors, time).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        time.Advance(Drain);
        primary.CompleteDispose();

        Assert.Equal(HidSessionFailureKind.Timeout, (await restore).Failure);
        Assert.Equal(1, actors.OpenCount);
    }

    [Fact]
    public async Task DeadlineDuringTimerCreation_WinsBeforeLaterDisposalCompletion()
    {
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        var time = new ImmediateDeadlineTimeProvider(primary.CompleteDispose);
        var actors = new ScriptedActorFactory(
            Actor(primary, new ScriptedClient(
                Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned))),
            Actor(new ScriptedSession(), new ScriptedClient(Unit())));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors, time).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        HidSessionResult<HidUnit> restore =
            await port.ApplyAsync(BatteryValidationSetting.Limit80, default);

        Assert.Equal(HidSessionFailureKind.Timeout, restore.Failure);
        Assert.Equal(1, actors.OpenCount);
    }

    [Fact]
    public async Task DisposalJustBeforeDeadline_Wins()
    {
        var time = new ManualTimeProvider();
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        var actors = RecoveryActors(primary);
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors, time).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        time.Advance(Drain - TimeSpan.FromTicks(1));
        primary.CompleteDispose();

        Assert.Null((await restore).Failure);
        Assert.Equal(2, actors.OpenCount);
    }

    [Fact]
    public async Task DeadlineCallbackAtExactBoundary_WinsBeforeQueuedDisposalObserver()
    {
        var time = new ManualTimeProvider();
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired, pendingDispose: true);
        time.Schedule(Drain, primary.CompleteDispose);
        var actors = RecoveryActors(primary);
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors, time).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        time.Advance(Drain);

        Assert.Equal(HidSessionFailureKind.Timeout, (await restore).Failure);
        Assert.Equal(1, actors.OpenCount);
    }

    [Fact]
    public async Task DisposalAfterBaseline_IsIdempotentAndDoesNoMoreIo()
    {
        var client = new ScriptedClient(Query(BatteryValidationSetting.Limit80));
        var session = new ScriptedSession();
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(session, client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);

        await port.DisposeAsync();
        await port.DisposeAsync();

        Assert.Equal(["Q1"], client.Events);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(HidSessionFailureKind.Disposed, (await port.QueryAsync(default)).Failure);
    }

    [Fact]
    public async Task CancellationWhileWaiting_DoesNotAdvanceOrConsumeCandidateQuery()
    {
        var client = new BlockingCandidateClient(BatteryValidationSetting.Limit80);
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        Task<HidSessionResult<HidUnit>> candidate = port.ApplyAsync(BatteryValidationSetting.Limit70, default);
        await client.CandidateEntered.Task;
        using var cancellation = new CancellationTokenSource();
        Task<HidSessionResult<BatteryValidationSetting>> losingQuery = port.QueryAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.Equal(HidSessionFailureKind.Canceled, (await losingQuery).Failure);
        client.ReleaseCandidate.TrySetResult();
        Assert.Null((await candidate).Failure);
        Assert.Equal(["Q1", "S2:Limit70"], client.Events);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentLosingApply_IsRejectedWithoutSecondIo()
    {
        var client = new BlockingCandidateClient(BatteryValidationSetting.Limit80);
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        Task<HidSessionResult<HidUnit>> winner = port.ApplyAsync(BatteryValidationSetting.Limit70, default);
        await client.CandidateEntered.Task;
        Task<HidSessionResult<HidUnit>> loser = port.ApplyAsync(BatteryValidationSetting.Limit65, default);
        client.ReleaseCandidate.TrySetResult();

        Assert.Null((await winner).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await loser).Failure);
        Assert.Equal(["Q1", "S2:Limit70"], client.Events);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task DisposeCallRace_SerializesWithoutDuplicateStageOrIdReuse()
    {
        var client = new BlockingCandidateClient(BatteryValidationSetting.Limit80);
        var session = new ScriptedSession();
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(session, client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        Task<HidSessionResult<HidUnit>> candidate = port.ApplyAsync(BatteryValidationSetting.Limit70, default);
        await client.CandidateEntered.Task;
        ValueTask disposal = port.DisposeAsync();
        client.ReleaseCandidate.TrySetResult();

        Assert.Null((await candidate).Failure);
        await disposal;
        Assert.Equal(["Q1", "S2:Limit70"], client.Events);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(HidSessionFailureKind.Disposed,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
    }

    [Fact]
    public async Task CandidateQueryFailure_StillAcceptsSavedBaselineOnly()
    {
        var client = new ScriptedClient(
            Query(BatteryValidationSetting.Limit80), Unit(),
            Failure<BatteryValidationSetting>(HidSessionFailureKind.NativeIoFailure), Unit());
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(),
            new ScriptedActorFactory(Actor(new ScriptedSession(), client))).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure, (await port.QueryAsync(default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit65, default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(["Q1", "S2:Limit70", "Q3", "S4:Limit80"], client.Events);
    }

    [Fact]
    public async Task RecoveryOpenFailure_IsExactAndDoesNoRestoreIo()
    {
        var client = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned));
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired);
        var actors = new ScriptedActorFactory(
            Actor(primary, client), Failure<ValidationActor>(HidSessionFailureKind.IdentityBlocked));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Equal(HidSessionFailureKind.IdentityBlocked,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        await port.DisposeAsync();
        Assert.Equal(2, actors.OpenCount);
        Assert.Equal(1, primary.DisposeCount);
        Assert.Equal(["Q1", "S2:Limit70"], client.Events);
    }

    [Fact]
    public async Task RecoveryOpenMixedResult_DisposesReturnedActorAndPreservesFailure()
    {
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired);
        var mixed = new ScriptedSession();
        var actors = new ScriptedActorFactory(
            Actor(primary, new ScriptedClient(
                Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned))),
            new HidSessionResult<ValidationActor>(
                Actor(mixed, new ScriptedClient()), HidSessionFailureKind.IdentityBlocked));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        HidSessionResult<HidUnit> restore =
            await port.ApplyAsync(BatteryValidationSetting.Limit80, default);
        await port.DisposeAsync();

        Assert.Equal(HidSessionFailureKind.IdentityBlocked, restore.Failure);
        Assert.Equal(1, primary.DisposeCount);
        Assert.Equal(1, mixed.DisposeCount);
        Assert.Equal(2, actors.OpenCount);
    }

    [Fact]
    public async Task RecoveryNullSuccess_IsNativeFailureWithoutRestoreIo()
    {
        var primary = new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired);
        var actors = new ScriptedActorFactory(
            Actor(primary, new ScriptedClient(
                Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned))),
            new HidSessionResult<ValidationActor>(null, null));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        await port.DisposeAsync();
        Assert.Equal(1, primary.DisposeCount);
        Assert.Equal(2, actors.OpenCount);
        Assert.Same(actors.Contexts[0], actors.Contexts[1]);
    }

    [Fact]
    public async Task RecoverySetFailure_SkipsQueryAndNeverRetriesOrReopens()
    {
        var primaryClient = new ScriptedClient(Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned));
        var recoveryClient = new ScriptedClient(Failure<HidUnit>(HidSessionFailureKind.ConflictBlocked));
        var recoverySession = new ScriptedSession();
        var actors = new ScriptedActorFactory(
            Actor(new ScriptedSession(RazerCommandSessionDisposition.RecoveryRequired), primaryClient),
            Actor(recoverySession, recoveryClient));
        IBatteryValidationPort port = (await Factory(new ValidationRunContext(), actors).OpenAsync(default)).Value!;
        await port.QueryAsync(default);
        await port.ApplyAsync(BatteryValidationSetting.Limit70, default);

        Assert.Equal(HidSessionFailureKind.ConflictBlocked,
            (await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(["S4:Limit80"], recoveryClient.Events);
        Assert.Equal(2, actors.OpenCount);
        Assert.Equal(1, recoverySession.DisposeCount);
        Assert.Equal(HidSessionFailureKind.Disposed, (await port.QueryAsync(default)).Failure);
    }

    private static BatteryValidationRunFactory Factory(
        ValidationRunContext context,
        ScriptedActorFactory actors,
        TimeProvider? time = null,
        PendingDisposalRegistry? registry = null) =>
        new(() => context, actors, time ?? TimeProvider.System, Drain, registry ?? new PendingDisposalRegistry());

    private static ScriptedActorFactory RecoveryActors(ScriptedSession primary) =>
        new(
            Actor(primary, new ScriptedClient(
                Query(BatteryValidationSetting.Limit80), Failure<HidUnit>(HidSessionFailureKind.Poisoned))),
            Actor(new ScriptedSession(), new ScriptedClient(Unit())));

    private static ValidationActor Actor(ScriptedSession session, IBatteryCommandClient client) => new(session, client);
    private static HidSessionResult<BatteryValidationSetting> Query(BatteryValidationSetting setting) => new(setting, null);
    private static HidSessionResult<HidUnit> Unit() => new(new HidUnit(), null);
    private static HidSessionResult<T> Failure<T>(HidSessionFailureKind failure) => new(default, failure);
    private static void Consume(ValidationRunContext context, int count)
    {
        for (int i = 0; i < count; i++) Assert.True(context.TryAllocateTransaction(out _));
    }

    private sealed class ScriptedActorFactory(params object[] actors) : IValidationActorFactory
    {
        private readonly Queue<object> actors = new(actors);
        internal int OpenCount { get; private set; }
        internal List<ValidationRunContext> Contexts { get; } = [];
        internal Action? OnOpen { get; init; }
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            OpenCount++;
            Contexts.Add(context);
            OnOpen?.Invoke();
            if (actors.Count == 0)
            {
                return ValueTask.FromResult(Failure<ValidationActor>(HidSessionFailureKind.NativeIoFailure));
            }

            object next = actors.Dequeue();
            return ValueTask.FromResult(next is ValidationActor actor
                ? new HidSessionResult<ValidationActor>(actor, null)
                : (HidSessionResult<ValidationActor>)next);
        }
    }

    private sealed class ScriptedClient(params object[] results) : IBatteryCommandClient
    {
        private readonly Queue<object> results = new(results);
        internal List<string> Events { get; } = [];
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken cancellationToken)
        {
            Events.Add($"Q{transactionId}");
            object next = results.Dequeue();
            return next is Exception error
                ? Task.FromException<HidSessionResult<BatteryValidationSetting>>(error)
                : Task.FromResult((HidSessionResult<BatteryValidationSetting>)next);
        }
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken cancellationToken)
        {
            Events.Add($"S{transactionId}:{setting}");
            object next = results.Dequeue();
            return next is Exception error
                ? Task.FromException<HidSessionResult<HidUnit>>(error)
                : Task.FromResult((HidSessionResult<HidUnit>)next);
        }
    }

    private sealed class ScriptedSession : IRazerCommandSession
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool pendingDispose;
        internal ScriptedSession(RazerCommandSessionDisposition disposition = RazerCommandSessionDisposition.Healthy, bool pendingDispose = false)
        { Disposition = disposition; this.pendingDispose = pendingDispose; }
        public RazerCommandSessionDisposition Disposition { get; private set; }
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command, Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (!pendingDispose) { Disposition = RazerCommandSessionDisposition.FullyDisposed; completion.TrySetResult(); }
            else Disposition = RazerCommandSessionDisposition.Disposing;
            return new ValueTask(completion.Task);
        }
        internal void CompleteDispose() { Disposition = RazerCommandSessionDisposition.FullyDisposed; completion.TrySetResult(); }
        internal void FailDispose(Exception error) { Disposition = RazerCommandSessionDisposition.FullyDisposed; completion.TrySetException(error); }
    }

    private sealed class BlockingCandidateClient(BatteryValidationSetting baseline) : IBatteryCommandClient
    {
        internal List<string> Events { get; } = [];
        internal TaskCompletionSource CandidateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCandidate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken cancellationToken)
        {
            Events.Add($"Q{transactionId}");
            return Task.FromResult(Query(baseline));
        }

        public async Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken cancellationToken)
        {
            Events.Add($"S{transactionId}:{setting}");
            CandidateEntered.TrySetResult();
            await ReleaseCandidate.Task;
            return Unit();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, now + dueTime);
            timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan amount)
        {
            now += amount;
            foreach (Timer timer in timers.ToArray()) timer.Fire(now);
        }
        internal void Schedule(TimeSpan due, Action action) =>
            _ = CreateTimer(_ => action(), null, due, Timeout.InfiniteTimeSpan);
        private sealed class Timer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { due = owner.now + dueTime; return true; }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            internal void Fire(DateTimeOffset now) { if (!disposed && now >= due) { disposed = true; callback(state); } }
        }
    }

    private sealed class ImmediateDeadlineTimeProvider(Action afterDeadline) : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            callback(state);
            afterDeadline();
            return new CompletedTimer();
        }

        private sealed class CompletedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
