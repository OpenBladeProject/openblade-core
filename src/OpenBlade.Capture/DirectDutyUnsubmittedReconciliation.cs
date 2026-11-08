namespace OpenBlade.Capture;

internal enum DirectDutySubmissionEvidence { Unknown, DefinitelyUnsubmitted, PossiblySubmitted, Submitted }

internal sealed record DirectDutyReconciliationSnapshot(
    DirectDutyOwner Owner,
    Guid PreviousAttemptId,
    long Generation,
    bool JournalComplete,
    bool PreviousSessionReleased,
    DirectDutySubmissionEvidence Submission,
    DirectDutyProof<DirectDutyEcState> Ec,
    DirectDutyProof<DirectDutyAccessState> Access);

internal enum DirectDutyReconciliationFailure { None, InvalidOwnership, AcquisitionFailed, AdmissionRefused, EvidenceRejected, ObservationRejected, StateChanged, JournalUncertain, Canceled }

internal sealed record DirectDutyReconciliationResult(
    DirectDutyReconciliationFailure Failure, bool FreshAttemptAuthorized, bool SessionReleaseFailed)
{
    public bool Success => Failure == DirectDutyReconciliationFailure.None && FreshAttemptAuthorized && !SessionReleaseFailed;
}

internal interface IDirectDutyReconciliationPort
{
    // Read-only hardware session: verify the durable owned-entry record and fresh exact
    // device/firmware/controller/power/session admission, then hold the same exclusion and
    // fencing used by exit. An unknown or still-active previous session cannot be acquired.
    Task<IDirectDutyReconciliationSession?> AcquireReconciliationSessionAsync(
        DirectDutyOwnership ownership, CancellationToken cancellationToken);
}

internal interface IDirectDutyReconciliationSession : IAsyncDisposable
{
    // Read the complete durable journal plus fresh independent EC then access observations.
    // DefinitelyUnsubmitted requires proof that every hardware submission boundary was
    // never crossed, or durable transport-proven NotSubmitted. An unmatched intent, missing
    // journal data, caller counters or matching EC values alone do not establish this.
    // All operations are bounded. None may open a gate, change bank or submit hardware writes.
    Task<DirectDutyReconciliationSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);

    // Atomically revalidate admission, observations, full owned-entry identity, the exact
    // prior attempt/generation and complete journal under exclusion. Durably fence the old
    // attempt before authorizing one fresh attempt of this operation. Old workers must fail
    // their per-write fence checks. False guarantees no authorization; exceptions are
    // ambiguous. Do not perform hardware writes or retry. Keep acquisition blocked until
    // this reconciliation session has been positively released.
    Task<bool> AuthorizeFreshAttemptAsync(DirectDutyReconciliationSnapshot snapshot, CancellationToken cancellationToken);

    // Disposal releases only the session; it cannot authorize reuse, clear a journal or write hardware.
}

// Explicit recovery only. RunAsync never calls this and never automatically retries an exit.
internal sealed class DirectDutyUnsubmittedReconciliation(IDirectDutyReconciliationPort port)
{
    private readonly IDirectDutyReconciliationPort port = port ?? throw new ArgumentNullException(nameof(port));
    private int started;

    public async Task<DirectDutyReconciliationResult> RunAsync(DirectDutyOwnership ownership, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0 || !DirectDutyAutomaticExit.ValidOwnership(ownership))
        {
            return new(DirectDutyReconciliationFailure.InvalidOwnership, false, false);
        }

        IDirectDutyReconciliationSession? session = null;
        DirectDutyReconciliationFailure failure = DirectDutyReconciliationFailure.None;
        bool authorized = false;
        bool releaseFailed = false;
        bool committing = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = await port.AcquireReconciliationSessionAsync(ownership, cancellationToken);
            if (session is null) { failure = DirectDutyReconciliationFailure.AdmissionRefused; }
            else
            {
                DirectDutyReconciliationSnapshot snapshot = await session.ReadSnapshotAsync(cancellationToken);
                failure = Validate(snapshot, ownership);
                if (failure == DirectDutyReconciliationFailure.None)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    committing = true;
                    authorized = await session.AuthorizeFreshAttemptAsync(snapshot, cancellationToken);
                    committing = false;
                    if (!authorized) { failure = DirectDutyReconciliationFailure.AdmissionRefused; }
                }
            }
        }
        catch (OperationCanceledException)
        {
            failure = committing ? DirectDutyReconciliationFailure.JournalUncertain : DirectDutyReconciliationFailure.Canceled;
        }
        catch (Exception)
        {
            failure = committing ? DirectDutyReconciliationFailure.JournalUncertain
                : session is null ? DirectDutyReconciliationFailure.AcquisitionFailed : DirectDutyReconciliationFailure.ObservationRejected;
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync(); }
                catch (Exception) { releaseFailed = true; }
            }
        }
        return new(failure, authorized, releaseFailed);
    }

    private static DirectDutyReconciliationFailure Validate(DirectDutyReconciliationSnapshot? snapshot, DirectDutyOwnership ownership)
    {
        if (snapshot is null || snapshot.Owner != ownership.Owner || snapshot.PreviousAttemptId == Guid.Empty
            || snapshot.Generation <= 0 || !snapshot.JournalComplete || !snapshot.PreviousSessionReleased
            || snapshot.Submission != DirectDutySubmissionEvidence.DefinitelyUnsubmitted)
        {
            return DirectDutyReconciliationFailure.EvidenceRejected;
        }
        if (!ValidProof(snapshot.Ec, ownership.Owner, 0)
            || !ValidProof(snapshot.Access, ownership.Owner, snapshot.Ec.Sequence))
        {
            return DirectDutyReconciliationFailure.ObservationRejected;
        }
        var expected = new DirectDutyEcState(true, null, ownership.AppliedManualFlags,
            (byte)(ownership.OriginalOverride | ownership.OwnedOverrideBits), ownership.AppliedDuty1, ownership.AppliedDuty2);
        return snapshot.Ec.State with { RetainedCustom = null } == expected
            && snapshot.Access.State == new DirectDutyAccessState(0, true)
                ? DirectDutyReconciliationFailure.None : DirectDutyReconciliationFailure.StateChanged;
    }

    private static bool ValidProof<T>(DirectDutyProof<T>? proof, DirectDutyOwner owner, long previousSequence) =>
        proof is not null && proof.Owner == owner && proof.Sequence > previousSequence
        && proof.Independent && proof.ExclusiveAndSettled && proof.Active && proof.State is not null;
}
