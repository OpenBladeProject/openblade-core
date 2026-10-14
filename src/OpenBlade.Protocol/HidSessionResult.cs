namespace OpenBlade.Protocol;

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

public enum HidProtocolValidationFailure
{
    ReportId,
    EnvelopeLength,
    Checksum,
    TransactionMismatch,
    CommandClassMismatch,
    CommandIdMismatch,
    Status,
    PayloadLength,
    PayloadValidator,
}

public readonly record struct HidSessionFailureDiagnostic(
    HidSessionFailurePhase Phase,
    HidSessionFailureKind Failure,
    int? NativeWin32Error,
    int? TransferredByteCount)
{
    public HidProtocolValidationFailure? ProtocolValidationFailure { get; init; }

    // Uppercase hexadecimal of a transaction-matched response payload whose
    // content the closed decoder rejected. It is bounded device-state
    // evidence only and is never populated for framing or channel failures.
    internal string? RejectedPayloadHex { get; init; }
}

public readonly record struct HidSessionResult<T>(
    T? Value,
    HidSessionFailureKind? Failure)
{
    public HidSessionFailureDiagnostic? Diagnostic { get; init; }
}
