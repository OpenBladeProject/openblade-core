using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum FanStopGuardianPhase { Idle, Starting, Stopped, ReturningToAutomatic, Completed, RecoveryRequired, Refused }

// Cached owner evidence, never reconstructed from a matching hardware value.
// StopConfirmed is historical for this operation, including a Stop followed by
// autonomous Auto before the service sees its first snapshot.
internal sealed record FanStopGuardianSnapshot(
    Guid OperationId,
    long Revision,
    FanStopGuardianPhase Phase,
    FanStopObservedState ConfirmedState = FanStopObservedState.Unknown,
    bool StopConfirmed = false,
    bool AutoConfirmed = false,
    bool CleanupConfirmed = false,
    bool JournalComplete = false,
    bool NativeReleased = false,
    bool OwnerExited = false,
    bool CommandPending = false,
    bool Unresolved = false,
    string? Reason = null,
    string? Failure = null)
{
    public PowerSource? ObservedPowerSource { get; init; }
    public FanStopEndReason EndReason { get; init; }
    public bool KnownNotSubmitted { get; init; }
    public bool Released => Enum.IsDefined(EndReason) && NativeReleased && OwnerExited && !CommandPending && !Unresolved && Failure is null
        && (Phase == FanStopGuardianPhase.Completed && AutoConfirmed && CleanupConfirmed && JournalComplete
            && ConfirmedState == FanStopObservedState.Automatic
            || Phase == FanStopGuardianPhase.Refused && KnownNotSubmitted && !StopConfirmed
                && ConfirmedState == FanStopObservedState.Ordinary);
}

internal interface IFanStopGuardianClient
{
    FanStopUnavailableReason UnavailableReason { get; }
    // Established without a device/mailbox read, before runtime construction.
    FanStopStartupDisposition StartupDisposition { get; }
    FanStopGuardianSnapshot Snapshot { get; }
    // Notification carries no authority. The consumer re-reads Snapshot.
    event Action? SnapshotChanged;
    Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token);
    Task<FanStopSubmission> StartAsync(Guid operationId, CancellationToken token);
    Task<FanStopSubmission> StartAsync(Guid operationId, FanStopSettings settings, CancellationToken token) =>
        settings.EndMode == FanStopEndMode.Auto ? StartAsync(operationId, token)
            : Task.FromResult(FanStopSubmission.NotSubmitted);
    Task<FanStopSubmission> RequestAutoAsync(Guid operationId, CancellationToken token);
}
