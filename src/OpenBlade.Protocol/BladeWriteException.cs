namespace OpenBlade.Protocol;

public enum WriteSubmissionState
{
    KnownNotApplied,
    Indeterminate,
}

internal enum BladeWriteFailurePhase
{
    Unspecified,
    ThermalPreWriteRead,
    AdjustableFanFirstManualEntrySubmission,
    AdjustableFanSecondManualEntrySubmission,
    HyperBoostAdapterEligibilityRead,
    ThermalFinalConfirmationRead,
}

public sealed class BladeWriteException : Exception
{
    public BladeWriteException(
        WriteSubmissionState submissionState,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SubmissionState = submissionState;
        FailurePhase = BladeWriteFailurePhase.Unspecified;
        (Diagnostic, StateQuery, CommandClass, CommandId) = ReadDiagnostic(innerException);
    }

    internal BladeWriteException(
        WriteSubmissionState submissionState,
        BladeWriteFailurePhase failurePhase,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SubmissionState = submissionState;
        FailurePhase = failurePhase;
        (Diagnostic, StateQuery, CommandClass, CommandId) = ReadDiagnostic(innerException);
    }

    public WriteSubmissionState SubmissionState { get; }

    internal BladeWriteFailurePhase FailurePhase { get; }

    internal HidSessionFailureDiagnostic? Diagnostic { get; init; }

    internal DeviceStateQuery? StateQuery { get; init; }

    internal byte? CommandClass { get; init; }

    internal byte? CommandId { get; init; }

    private static (HidSessionFailureDiagnostic? Diagnostic, DeviceStateQuery? Query,
        byte? CommandClass, byte? CommandId) ReadDiagnostic(Exception? innerException) =>
        innerException switch
        {
            NativeDeviceReadException read => (read.Diagnostic, read.StateQuery, null, null),
            BladeWriteException write =>
                (write.Diagnostic, write.StateQuery, write.CommandClass, write.CommandId),
            _ => default,
        };
}
