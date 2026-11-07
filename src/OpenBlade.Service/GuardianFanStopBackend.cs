using OpenBlade.Contracts;

namespace OpenBlade.Service;

// Called under the service operation gate. The transport owns authentication,
// durable startup admission and the native owner; this adapter cannot adopt an
// existing owner merely because its cached state looks like our requested mode.
internal sealed class GuardianFanStopBackend(IFanStopGuardianClient client) : IFanStopBackend, IAsyncDisposable
{
    private Guid operationId;
    private FanStopGuardianSnapshot? accepted;
    private bool autoRequested;
    private bool fenced;
    private bool dispatchPending;
    private IDisposable? hardwareLease;
    private Func<CancellationToken, Task<IDisposable>>? acquireHardware;

    public void ConfigureHardwareDelegation(Func<CancellationToken, Task<IDisposable>> acquire)
    {
        if (HasOperation || acquireHardware is not null) { throw new InvalidOperationException("Guardian hardware delegation is already configured."); }
        acquireHardware = acquire;
    }

    public FanStopUnavailableReason UnavailableReason => client.UnavailableReason;
    public FanStopStartupDisposition StartupDisposition => client.StartupDisposition;
    public event Action? SnapshotChanged
    {
        add => client.SnapshotChanged += value;
        remove => client.SnapshotChanged -= value;
    }
    public bool HasOperation => operationId != Guid.Empty;
    public bool BlocksOrdinaryHardware => fenced || StartupDisposition != FanStopStartupDisposition.Clear
        || hardwareLease is not null || HasOperation && accepted?.Released != true;

    public FanStopGuardianSnapshot Snapshot
    {
        get
        {
            FanStopGuardianSnapshot next = client.Snapshot;
            if (fenced) { return Failed("Guardian evidence previously became inconsistent."); }
            if (!HasOperation)
            {
                if (next.OperationId != Guid.Empty || next.Phase != FanStopGuardianPhase.Idle)
                {
                    fenced = true;
                    return Failed("An unowned guardian operation cannot be adopted.");
                }
                return next;
            }
            if (dispatchPending && next.OperationId != operationId)
            {
                return new(operationId, 0, FanStopGuardianPhase.Starting, Reason: "Guardian dispatch is pending.");
            }
            if (next.OperationId != operationId || next.Revision < 0
                || accepted is not null && (next.Revision < accepted.Revision
                    || next.Revision == accepted.Revision && next != accepted
                    || accepted.StopConfirmed && !next.StopConfirmed
                    || accepted.Released && !next.Released))
            {
                fenced = true;
                return Failed("Guardian operation identity or monotonic evidence changed.");
            }
            accepted = next;
            if (next.Released) { Interlocked.Exchange(ref hardwareLease, null)?.Dispose(); }
            return next;
        }
    }

    public Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token) => client.ReadAdmissionAsync(token);
    public Task<FanStopObservedState> ReadStateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot.ConfirmedState);
    }

    public async Task<FanStopSubmission> SubmitAsync(FanStopMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mode == FanStopMode.Automatic)
        {
            if (!HasOperation || Snapshot.Released) { return FanStopSubmission.NotSubmitted; }
            if (fenced || Snapshot.Unresolved) { return FanStopSubmission.Uncertain; }
            if (autoRequested) { return FanStopSubmission.Submitted; }
            autoRequested = true;
            // A lost response must never cause a second Auto request. The owner
            // also latches Auto and independently handles client disconnect.
            return await client.RequestAutoAsync(operationId, token).ConfigureAwait(false);
        }
        if (mode != FanStopMode.Stop) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        if (fenced || StartupDisposition != FanStopStartupDisposition.Clear
            || HasOperation && !Snapshot.Released)
        {
            return FanStopSubmission.NotSubmitted;
        }
        // Check unowned evidence before allocating a fresh operation.
        if (!HasOperation && Snapshot.Unresolved) { return FanStopSubmission.Uncertain; }
        Guid previousOperation = operationId;
        FanStopGuardianSnapshot? previousEvidence = accepted;
        operationId = Guid.NewGuid();
        accepted = null;
        autoRequested = false;
        dispatchPending = true;
        bool dispatched = false;
        // Keep the ID even if dispatch throws: cancellation is not evidence of
        // no acquisition. Subsequent snapshots must resolve this exact run.
        try
        {
            if (acquireHardware is null) { throw new InvalidOperationException("Exclusive guardian hardware delegation is required."); }
            hardwareLease = await acquireHardware(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            dispatched = true;
            FanStopSubmission result = await client.StartAsync(operationId, token).ConfigureAwait(false);
            if (result == FanStopSubmission.NotSubmitted)
            {
                Interlocked.Exchange(ref hardwareLease, null)?.Dispose();
                operationId = previousOperation;
                accepted = previousEvidence;
            }
            return result;
        }
        catch when (!dispatched)
        {
            Interlocked.Exchange(ref hardwareLease, null)?.Dispose();
            operationId = previousOperation;
            accepted = previousEvidence;
            throw;
        }
        finally { dispatchPending = false; }
    }

    private FanStopGuardianSnapshot Failed(string reason) => new(operationId,
        accepted?.Revision ?? 0, FanStopGuardianPhase.RecoveryRequired,
        StopConfirmed: accepted?.StopConfirmed ?? false, Unresolved: true, Failure: reason);

    public async ValueTask DisposeAsync()
    {
        // Disposing the service transport requests Auto, never terminates its
        // owner. A timeout cannot release the native exclusion lease.
        if (client is IAsyncDisposable disposable) { await disposable.DisposeAsync().ConfigureAwait(false); }
        _ = Snapshot;
    }
}
