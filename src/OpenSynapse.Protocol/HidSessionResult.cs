namespace OpenSynapse.Protocol;

public enum HidSessionFailureKind
{
    IdentityBlocked,
    ConflictBlocked,
    ConflictDetectionFailed,
    QueuedCanceled,
    Canceled,
    Poisoned,
    Exhausted,
    Disposed,
    Timeout,
    NativeIoFailure,
    ProtocolFailure,
}

public readonly record struct HidUnit;

public enum HidSessionFailurePhase
{
    Guard,
    SetSubmit,
    SetCompletion,
    GetSubmit,
    GetCompletion,
    ProtocolValidation,
}

public readonly record struct HidSessionFailureDiagnostic(
    HidSessionFailurePhase Phase,
    HidSessionFailureKind Failure,
    int? NativeWin32Error,
    int? TransferredByteCount);

public readonly record struct HidSessionResult<T>(
    T? Value,
    HidSessionFailureKind? Failure)
{
    public HidSessionFailureDiagnostic? Diagnostic { get; init; }
}
