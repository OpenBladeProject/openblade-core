using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal readonly record struct ServiceTransitionWriteProgress(
    bool Attempted, bool ConfirmationUnavailable, bool Confirmed);

// Persistence belongs to the caller. These facts describe only hardware application,
// including partial completion before a later refusal or unconfirmed submission.
internal sealed record ServiceTransitionApplicationOutcome(
    bool FullyApplied,
    bool StopFurtherWrites,
    bool SubmissionPossible,
    bool AnyConfirmedWrites,
    string Message,
    FullTransitionApplyOutcome? Subsystems = null,
    bool AnyWritesAttempted = false)
{
    internal static ServiceTransitionApplicationOutcome Interrupted(
        string message, bool submissionPossible = false) =>
        new(false, true, submissionPossible, false, message);

    internal static ServiceTransitionApplicationOutcome Interrupted(
        string message, ServiceTransitionWriteProgress progress) =>
        new(false, true, progress.ConfirmationUnavailable, progress.Confirmed, message,
            AnyWritesAttempted: progress.Attempted);

    internal static ServiceTransitionApplicationOutcome From(
        FullTransitionApplyOutcome outcome, LaptopControlBatchOutcome laptopControls,
        bool laptopSubmissionPossible, ServiceTransitionWriteProgress boundary, string message)
    {
        ControlResult[] results =
        [
            outcome.Results.Performance, outcome.Results.Fans,
            outcome.Results.Battery, outcome.Results.Lighting,
        ];
        bool uncertain = outcome.ConfirmationUnavailable?.Count > 0
            || laptopSubmissionPossible || boundary.ConfirmationUnavailable;
        bool complete = laptopControls.FullyApplied && !outcome.DeviceAccessFailed
            && !uncertain && outcome.DeferredNotSubmitted?.Count is not > 0
            && results.All(result => result.Status is ControlResultStatus.Applied or ControlResultStatus.Unchanged);
        return new(complete, uncertain || outcome.DeviceAccessFailed || laptopControls.StopFurtherWrites,
            uncertain, outcome.ReadbackMatched.Any(outcome.WriteAttempted.Contains)
                || laptopControls.AnyConfirmedWrites || boundary.Confirmed,
            message, outcome, outcome.WriteAttempted.Count > 0
                || laptopControls.AnyWritesAttempted || boundary.Attempted);
    }

    internal ControlOperationResult ToOperationResult(bool intentStored = false) => new(
        FullyApplied ? ControlOperationStatus.Applied
            : intentStored ? ControlOperationStatus.Stored
            : SubmissionPossible || AnyConfirmedWrites || AnyWritesAttempted ? ControlOperationStatus.Failed
            : ControlOperationStatus.Blocked,
        Message);
}
