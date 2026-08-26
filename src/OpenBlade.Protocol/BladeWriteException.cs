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
    }

    public WriteSubmissionState SubmissionState { get; }

    internal BladeWriteFailurePhase FailurePhase { get; }
}
