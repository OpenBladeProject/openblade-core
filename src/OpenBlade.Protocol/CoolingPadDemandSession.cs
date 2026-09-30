using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public enum CoolingPadRawControlState { Raw0000, Raw0300, Other }
public sealed record CoolingPadRawQueryResult(CoolingPadRawControlState State, CoolingPadSessionFault? Fault);
public sealed record CoolingPadWriteResult(bool Attempted, bool Acknowledged, CoolingPadSessionFault? Fault);
public sealed record CoolingPadDemandEntryResult(CoolingPadWriteResult Entry, CoolingPadWriteResult Demand);

/// <summary>
/// Typed finite developer operations; echoes are not fan-state or ownership readback.
/// Raw restoration is explicit and does not restore fan behavior or prove handback.
/// </summary>
public interface ICoolingPadDemandSession : IAsyncDisposable
{
    Task<CoolingPadRawQueryResult> QueryRawControlStateAsync(CancellationToken cancellationToken);
    Task<CoolingPadWriteResult> AcquireAsync(Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task<CoolingPadDemandEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand,
        Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task<CoolingPadWriteResult> UpdateDemandAsync(CoolingPadConfiguredRpmDemand demand,
        Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task<CoolingPadWriteResult> ReassertMediumAsync(Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task<CoolingPadWriteResult> ReassertQuietAsync(Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task<CoolingPadWriteResult> RestoreRawControlStateAsync(Func<bool> submissionAllowed, CancellationToken cancellationToken);
}

internal sealed class CoolingPadDemandSession(ValidationActor actor, ValidationRunContext context,
    TimeProvider clock, PendingDisposalRegistry pending, Action? fullyDisposed = null) : ICoolingPadDemandSession
{
    private readonly SemaphoreSlim serial = new(1);
    private volatile bool stopped;
    private bool baselineSaved;
    private bool acquired;
    private bool entered;
    private bool budgetExhausted;
    private bool restorationStarted;
    private bool mediumAcknowledged;
    private bool quietAcknowledged;
    // Never wrap IDs. With five startup exchanges and four reserved restoration
    // exchanges, a fresh context has at most 246 demand updates (extra queries consume IDs).
    private readonly byte[]? restorationIds = ReserveRestoration(context);

    private static byte[]? ReserveRestoration(ValidationRunContext context)
    {
        if (context.RemainingTransactions < 4) { return null; }
        context.TryAllocateTransaction(out byte set);
        context.TryAllocateTransaction(out byte query);
        context.TryAllocateTransaction(out byte medium);
        context.TryAllocateTransaction(out byte quiet);
        return [set, query, medium, quiet];
    }

    public async Task<CoolingPadRawQueryResult> QueryRawControlStateAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            stopped = true;
            return new(CoolingPadRawControlState.Other, CoolingPadSessionFault.Rejected);
        }
        if (!serial.Wait(0))
        {
            return new(CoolingPadRawControlState.Other, CoolingPadSessionFault.Rejected);
        }
        try
        {
            var query = await QueryCore(cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                baselineSaved = query.Fault is null && query.State == CoolingPadRawControlState.Raw0000;
            }
            return query;
        }
        finally { serial.Release(); }
    }

    public Task<CoolingPadWriteResult> AcquireAsync(Func<bool> submissionAllowed, CancellationToken cancellationToken) =>
        Serialized(async () =>
        {
            if (!baselineSaved || acquired) { return Rejected(); }
            if (!Reserve(2, out byte[] ids)) { return Rejected(); }
            var write = await Write(Echo(0x00, 0x04, [0x03, 0x00]), ids[0], submissionAllowed, cancellationToken).ConfigureAwait(false);
            if (!write.Acknowledged) { return write; }
            var query = await QueryCore(cancellationToken, ids[1]).ConfigureAwait(false);
            if (query.Fault is not null || query.State != CoolingPadRawControlState.Raw0300)
            {
                stopped = true;
                return write with { Fault = query.Fault ?? CoolingPadSessionFault.Rejected };
            }
            acquired = true;
            return write;
        }, cancellationToken);

    public async Task<CoolingPadDemandEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand,
        Func<bool> submissionAllowed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submissionAllowed);
        if (cancellationToken.IsCancellationRequested) { stopped = true; }
        if (!demand.IsWellFormed || stopped || !serial.Wait(0))
        {
            return new(Rejected(), Rejected());
        }
        try
        {
            if (!acquired || entered || !Reserve(2, out byte[] ids)) { return new(Rejected(), Rejected()); }
            var entry = await Write(Echo(0x0D, 0x10, [0x00, 0x06, 0x00]), ids[0], submissionAllowed, cancellationToken).ConfigureAwait(false);
            if (!entry.Acknowledged) { return new(entry, Rejected()); }
            // No pause, callback, retry or other exchange between entry echo and demand.
            var target = await Write(CoolingPadHostDemandProtocol.CreateSpec(demand), ids[1], submissionAllowed, cancellationToken).ConfigureAwait(false);
            entered = target.Acknowledged && target.Fault is null;
            return new(entry, target);
        }
        finally { serial.Release(); }
    }

    public Task<CoolingPadWriteResult> UpdateDemandAsync(CoolingPadConfiguredRpmDemand demand,
        Func<bool> submissionAllowed, CancellationToken cancellationToken) => Serialized(async () =>
        {
            if (!entered || restorationStarted || !demand.IsWellFormed || !Reserve(1, out byte[] ids)) { return Rejected(); }
            return await Write(CoolingPadHostDemandProtocol.CreateSpec(demand), ids[0], submissionAllowed, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<CoolingPadWriteResult> ReassertMediumAsync(Func<bool> submissionAllowed,
        CancellationToken cancellationToken) => Serialized(async () =>
        {
            if (!baselineSaved || !acquired || !entered || restorationStarted || restorationIds is not { } ids)
            { return Rejected(); }
            restorationStarted = true;
            var write = await Write(Echo(0x0D, 0x10, [0x01, 0x01, 0x2C]), ids[2],
                submissionAllowed, cancellationToken).ConfigureAwait(false);
            mediumAcknowledged = write.Acknowledged && write.Fault is null;
            return write;
        }, cancellationToken);

    public Task<CoolingPadWriteResult> ReassertQuietAsync(Func<bool> submissionAllowed,
        CancellationToken cancellationToken) => Serialized(async () =>
        {
            if (!baselineSaved || !acquired || !entered || !mediumAcknowledged || quietAcknowledged
                || restorationIds is not { } ids) { return Rejected(); }
            var write = await Write(Echo(0x0D, 0x10, [0x00, 0x06, 0x00]), ids[3],
                submissionAllowed, cancellationToken).ConfigureAwait(false);
            quietAcknowledged = write.Acknowledged && write.Fault is null;
            return write;
        }, cancellationToken);

    public Task<CoolingPadWriteResult> RestoreRawControlStateAsync(Func<bool> submissionAllowed,
        CancellationToken cancellationToken) => Serialized(async () =>
        {
            if (!baselineSaved || !acquired || restorationStarted && !quietAcknowledged
                || restorationIds is not { } ids) { return Rejected(); }
            var write = await Write(Echo(0x00, 0x04, [0x00, 0x00]), ids[0], submissionAllowed, cancellationToken).ConfigureAwait(false);
            if (!write.Acknowledged) { return write; }
            var query = await QueryCore(cancellationToken, ids[1]).ConfigureAwait(false);
            stopped = true;
            return query.Fault is null && query.State == CoolingPadRawControlState.Raw0000
                ? write : write with { Fault = query.Fault ?? CoolingPadSessionFault.Rejected };
        }, cancellationToken);

    private async Task<CoolingPadWriteResult> Serialized(Func<Task<CoolingPadWriteResult>> action, CancellationToken token)
    {
        if (token.IsCancellationRequested) { stopped = true; }
        if (stopped || !serial.Wait(0)) { return Rejected(); }
        try { return await action().ConfigureAwait(false); }
        catch { stopped = true; return new(true, false, CoolingPadSessionFault.SubmissionUnknown); }
        finally { serial.Release(); }
    }

    private bool Reserve(int count, out byte[] ids)
    {
        ids = new byte[count];
        if (budgetExhausted || restorationIds is null || context.RemainingTransactions < count)
        {
            budgetExhausted = true;
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!context.TryAllocateTransaction(out ids[i])) { budgetExhausted = true; return false; }
        }
        return true;
    }

    private static RazerCommandSpec Echo(byte cls, byte command, byte[] args) =>
        new(cls, command, args, args.Length, payload => payload.Span.SequenceEqual(args));

    private async Task<CoolingPadRawQueryResult> QueryCore(CancellationToken token, byte? reserved = null)
    {
        if (stopped || token.IsCancellationRequested) { stopped = true; return new(CoolingPadRawControlState.Other, CoolingPadSessionFault.Rejected); }
        if (reserved is null)
        {
            if (!Reserve(1, out byte[] ids)) { return new(CoolingPadRawControlState.Other, CoolingPadSessionFault.Rejected); }
            reserved = ids[0];
        }
        try
        {
            var response = await ExecuteBounded(new(0x00, 0x84, new byte[] { 0, 0 }, 2,
                p => p.Length == 2 && p.Span[1] == 0 && p.Span[0] is 0 or 3),
                reserved.Value, token).ConfigureAwait(false);
            if (response.Failure is not null || response.Value.Length != 2 || response.Value.Span[1] != 0 || response.Value.Span[0] is not (0 or 3))
            {
                stopped = true;
                return new(CoolingPadRawControlState.Other, MapFault(response.Failure));
            }
            return new(response.Value.Span[0] == 0 ? CoolingPadRawControlState.Raw0000 : CoolingPadRawControlState.Raw0300, null);
        }
        catch { stopped = true; return new(CoolingPadRawControlState.Other, CoolingPadSessionFault.Disconnected); }
    }

    private async Task<CoolingPadWriteResult> Write(RazerCommandSpec command, byte id, Func<bool> allowed, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (stopped || token.IsCancellationRequested) { stopped = true; return Rejected(); }
        try
        {
            var response = await ExecuteBounded(command with
            {
                // Recheck after the supplied callback too: it may itself block.
                SubmissionAllowed = () => !stopped && !token.IsCancellationRequested
                    && allowed() && !stopped && !token.IsCancellationRequested,
            }, id, token).ConfigureAwait(false);
            if (response.Failure is null && command.PayloadValidator?.Invoke(response.Value) == true) { return new(true, true, null); }
            stopped = true;
            bool notSubmitted = response.Failure is HidSessionFailureKind.IdentityBlocked or HidSessionFailureKind.ConflictBlocked
                or HidSessionFailureKind.ConflictDetectionFailed or HidSessionFailureKind.QueuedCanceled
                or HidSessionFailureKind.Exhausted or HidSessionFailureKind.Disposed or HidSessionFailureKind.Poisoned;
            return new(!notSubmitted, false, notSubmitted ? MapFault(response.Failure) : CoolingPadSessionFault.SubmissionUnknown);
        }
        catch { stopped = true; return new(true, false, CoolingPadSessionFault.SubmissionUnknown); }
    }

    private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteBounded(
        RazerCommandSpec command, byte id, CancellationToken token)
    {
        // Also isolate a synchronous guard/implementation before it returns its Task.
        Task<HidSessionResult<ReadOnlyMemory<byte>>> operation = Task.Run(async () =>
            await actor.Session.ExecuteAsync(command, () => TransactionIdAssignment.Valid(id),
                TimeSpan.FromSeconds(3), token).ConfigureAwait(false), CancellationToken.None);
        try
        {
            return await operation.WaitAsync(TimeSpan.FromSeconds(3), clock, token).ConfigureAwait(false);
        }
        catch
        {
            stopped = true;
            pending.Retain(operation);
            throw;
        }
    }

    private static CoolingPadWriteResult Rejected() => new(false, false, CoolingPadSessionFault.Rejected);
    internal static CoolingPadSessionFault MapFault(HidSessionFailureKind? failure) => failure switch
    {
        HidSessionFailureKind.ConflictBlocked or HidSessionFailureKind.ConflictDetectionFailed => CoolingPadSessionFault.ControllerConflict,
        HidSessionFailureKind.IdentityBlocked or HidSessionFailureKind.Disposed => CoolingPadSessionFault.Disconnected,
        _ => CoolingPadSessionFault.Rejected,
    };

    public async ValueTask DisposeAsync()
    {
        stopped = true;
        await BoundedValidationActorDisposal.DisposeAsync(new DeferredDisposal(actor, () =>
        {
            if (actor.Session.Disposition == RazerCommandSessionDisposition.FullyDisposed) { fullyDisposed?.Invoke(); }
        }), clock,
            TimeSpan.FromSeconds(5), pending).ConfigureAwait(false);
    }

    internal sealed class DeferredDisposal(IAsyncDisposable target, Action? completed = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(Task.Run(async () =>
        {
            await target.DisposeAsync().ConfigureAwait(false);
            completed?.Invoke();
        }));
    }
}
