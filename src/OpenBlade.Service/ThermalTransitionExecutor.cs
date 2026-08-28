using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal enum ThermalTransitionStage
{
    Preflight = 0,
    Submission = 1,
    Acknowledgement = 2,
    Confirmation = 3,
}

internal enum ThermalSubmissionProgress
{
    NotSubmitted = 0,
    Submitted = 1,
    Acknowledged = 2,
}

internal enum ThermalTransitionDisposition
{
    Confirmed = 0,
    External = 1,
    ReconciliationUnavailable = 2,
    Blocked = 3,
    Failed = 4,
}

internal sealed record ThermalTransitionTrace(
    Guid AttemptId,
    IReadOnlyList<DeviceSubsystem> Components,
    ThermalTransitionStage Stage,
    ThermalSubmissionProgress SubmissionProgress,
    WriteSubmissionState? Certainty,
    ThermalTransitionDisposition Disposition,
    int AttemptCount,
    bool RetryConsumed,
    string? RetryBlockReason);

internal sealed record ThermalTransitionExecution(
    ScopedApplyOutcome Outcome,
    ThermalTransitionTrace Trace);

internal sealed record ThermalRetryAdmission(
    bool Allowed,
    IBladeDevice? Device,
    string? BlockReason)
{
    public static ThermalRetryAdmission Allow(IBladeDevice device) => new(
        Allowed: true,
        Device: device ?? throw new ArgumentNullException(nameof(device)),
        BlockReason: null);

    public static ThermalRetryAdmission Block(string reason) => new(
        Allowed: false,
        Device: null,
        BlockReason: string.IsNullOrWhiteSpace(reason)
            ? "Thermal retry admission was not renewed."
            : reason);
}

internal static class ThermalTransitionExecutor
{
    private static readonly DeviceSubsystem[] ScopedPerformanceComponents =
        [DeviceSubsystem.Performance];

    public static async Task<ThermalTransitionExecution>
        ExecuteScopedPerformanceAsync(
        IBladeDevice initialDevice,
        Func<IBladeDevice, CancellationToken, Task<ScopedApplyOutcome>> applyOnce,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>> revalidateRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialDevice);
        ArgumentNullException.ThrowIfNull(applyOnce);
        ArgumentNullException.ThrowIfNull(revalidateRetry);

        Guid attemptId = Guid.NewGuid();
        int attemptCount = 1;
        bool retryConsumed = false;
        ScopedApplyOutcome outcome = await applyOnce(initialDevice, cancellationToken)
            .ConfigureAwait(false);
        if (!IsEligibleForFreshSessionRetry(outcome))
        {
            return CreateExecution(
                attemptId,
                outcome,
                attemptCount,
                retryConsumed,
                retryBlockReason: null);
        }

        // The lease is consumed before volatile admission is repeated so a
        // refusal, cancellation, or fault cannot mint another retry.
        retryConsumed = true;
        ThermalRetryAdmission admission;
        try
        {
            admission = await revalidateRetry(
                    attemptId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            admission = ThermalRetryAdmission.Block(
                $"Fresh-session retry admission failed: {exception.Message}");
        }

        if (!admission.Allowed || admission.Device is null)
        {
            string blockReason = admission.BlockReason
                ?? "Thermal retry admission was not renewed.";
            outcome = outcome with
            {
                Result = outcome.Result with
                {
                    Message = $"{outcome.Result.Message} No retry was submitted: {blockReason}",
                },
            };
            return CreateExecution(
                attemptId,
                outcome,
                attemptCount,
                retryConsumed,
                blockReason,
                blockedDuringRetryPreflight: true);
        }

        attemptCount++;
        outcome = await applyOnce(admission.Device, cancellationToken)
            .ConfigureAwait(false);
        return CreateExecution(
            attemptId,
            outcome,
            attemptCount,
            retryConsumed,
            retryBlockReason: null);
    }

    private static bool IsEligibleForFreshSessionRetry(ScopedApplyOutcome outcome) =>
        outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied
        && outcome.WriteFailurePhase == BladeWriteFailurePhase.ThermalPreWriteRead;

    private static ThermalTransitionExecution CreateExecution(
        Guid attemptId,
        ScopedApplyOutcome outcome,
        int attemptCount,
        bool retryConsumed,
        string? retryBlockReason,
        bool blockedDuringRetryPreflight = false)
    {
        ThermalSubmissionProgress submissionProgress = SubmissionProgress(outcome);
        ThermalTransitionStage stage = Stage(outcome, submissionProgress);
        ThermalTransitionDisposition disposition = blockedDuringRetryPreflight
            ? ThermalTransitionDisposition.Blocked
            : Disposition(outcome);
        if (blockedDuringRetryPreflight)
        {
            stage = ThermalTransitionStage.Preflight;
        }

        return new ThermalTransitionExecution(
            outcome,
            new ThermalTransitionTrace(
                attemptId,
                ScopedPerformanceComponents,
                stage,
                submissionProgress,
                outcome.WriteSubmissionState,
                disposition,
                attemptCount,
                retryConsumed,
                retryBlockReason));
    }

    private static ThermalSubmissionProgress SubmissionProgress(
        ScopedApplyOutcome outcome)
    {
        if (outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied)
        {
            return ThermalSubmissionProgress.NotSubmitted;
        }

        if (outcome.WriteSubmissionState == WriteSubmissionState.Indeterminate)
        {
            return ThermalSubmissionProgress.Submitted;
        }

        if (!outcome.WriteAttempted)
        {
            return ThermalSubmissionProgress.NotSubmitted;
        }

        return outcome.Readback is not null || outcome.Result.Status == ControlResultStatus.Applied
            ? ThermalSubmissionProgress.Acknowledged
            : ThermalSubmissionProgress.Submitted;
    }

    private static ThermalTransitionStage Stage(
        ScopedApplyOutcome outcome,
        ThermalSubmissionProgress submissionProgress)
    {
        if (outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied)
        {
            return ThermalTransitionStage.Preflight;
        }

        if (outcome.Readback is not null)
        {
            return ThermalTransitionStage.Confirmation;
        }

        return submissionProgress switch
        {
            ThermalSubmissionProgress.Acknowledged =>
                ThermalTransitionStage.Acknowledgement,
            ThermalSubmissionProgress.Submitted => ThermalTransitionStage.Submission,
            _ when outcome.WriteSubmissionState is not null =>
                ThermalTransitionStage.Submission,
            _ => ThermalTransitionStage.Preflight,
        };
    }

    private static ThermalTransitionDisposition Disposition(
        ScopedApplyOutcome outcome)
    {
        if (outcome.ReadbackMatched
            && outcome.Result.Status is
                ControlResultStatus.Applied or ControlResultStatus.Unchanged)
        {
            return ThermalTransitionDisposition.Confirmed;
        }

        if (outcome.ConfirmationUnavailable)
        {
            return ThermalTransitionDisposition.ReconciliationUnavailable;
        }

        if (outcome.Readback is not null
            && (outcome.WriteAttempted
                || outcome.WriteSubmissionState == WriteSubmissionState.Indeterminate))
        {
            return ThermalTransitionDisposition.External;
        }

        if (outcome.Result.Status == ControlResultStatus.Blocked)
        {
            return ThermalTransitionDisposition.Blocked;
        }

        return ThermalTransitionDisposition.Failed;
    }
}
