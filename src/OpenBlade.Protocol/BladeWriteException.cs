namespace OpenBlade.Protocol;

public enum WriteSubmissionState
{
    KnownNotApplied,
    Indeterminate,
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
    }

    public WriteSubmissionState SubmissionState { get; }

    internal HidSessionFailureKind? FailureKind { get; init; }
}
