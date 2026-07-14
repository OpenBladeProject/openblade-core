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

public readonly record struct HidSessionResult<T>(
    T? Value,
    HidSessionFailureKind? Failure);
