namespace OpenBlade.Protocol;

internal sealed class NativeOperationFinalizationException : Exception
{
    internal NativeOperationFinalizationException(
        HidFeatureIoResult result,
        Exception innerException)
        : base("Native overlapped I/O resource finalization failed.", innerException)
    {
        Result = result;
    }

    internal HidFeatureIoResult Result { get; }
}

internal sealed class NativeUseLease : IDisposable
{
    private Action? releaseAction;

    internal NativeUseLease(Action release)
    {
        releaseAction = release;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref releaseAction, null)?.Invoke();
    }
}

internal sealed class NativeOperationState
{
    private readonly object sync = new();
    private readonly Action finalizeResourcesAction;
    private readonly TaskCompletionSource<HidFeatureIoResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SubmissionState submissionState = SubmissionState.Allocated;
    private NativeIoCompletion? nativeCompletion;
    private HidFeatureTermination recordedTermination = HidFeatureTermination.Completed;
    private int nativeUseLeaseCount;
    private bool completionObserved;
    private bool abandonedBeforeSubmission;
    private bool targetedCancellationClaimed;

    public NativeOperationState(Action finalizeResources)
    {
        ArgumentNullException.ThrowIfNull(finalizeResources);
        finalizeResourcesAction = finalizeResources;
    }

    public Task<HidFeatureIoResult> Completion => completion.Task;

    public bool TryBeginSubmission(out NativeUseLease lease)
    {
        lock (sync)
        {
            if (submissionState != SubmissionState.Allocated)
            {
                lease = null!;
                return false;
            }

            submissionState = SubmissionState.Submitting;
            nativeUseLeaseCount++;
            lease = new NativeUseLease(ReleaseNativeUseLease);
            return true;
        }
    }

    public void RecordSubmitted()
    {
        Finalization? finalization = null;
        lock (sync)
        {
            if (submissionState != SubmissionState.Submitting || abandonedBeforeSubmission)
            {
                return;
            }

            submissionState = SubmissionState.Submitted;
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    public void AbandonBeforeSubmission(HidFeatureTermination termination)
    {
        ValidateCancellationTermination(termination);
        Finalization? finalization = null;
        lock (sync)
        {
            if (submissionState is SubmissionState.Submitted or SubmissionState.Finalized)
            {
                return;
            }

            abandonedBeforeSubmission = true;
            RecordCancellationIntentLocked(termination);
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    public void RecordImmediateFailure(NativeIoCompletion failure)
    {
        Finalization? finalization = null;
        lock (sync)
        {
            if (submissionState != SubmissionState.Submitting || abandonedBeforeSubmission)
            {
                return;
            }

            submissionState = SubmissionState.Submitted;
            if (!completionObserved)
            {
                nativeCompletion = failure;
                completionObserved = true;
            }

            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    public void RecordCancellationIntent(HidFeatureTermination termination)
    {
        ValidateCancellationTermination(termination);
        lock (sync)
        {
            RecordCancellationIntentLocked(termination);
        }
    }

    public bool TryAcquireCancellationLease(out NativeUseLease lease)
    {
        lock (sync)
        {
            if (submissionState != SubmissionState.Submitted ||
                completionObserved ||
                recordedTermination == HidFeatureTermination.Completed)
            {
                lease = null!;
                return false;
            }

            nativeUseLeaseCount++;
            lease = new NativeUseLease(ReleaseNativeUseLease);
            return true;
        }
    }

    public bool TryClaimTargetedCancellation()
    {
        lock (sync)
        {
            if (submissionState != SubmissionState.Submitted ||
                completionObserved ||
                recordedTermination == HidFeatureTermination.Completed ||
                targetedCancellationClaimed)
            {
                return false;
            }

            targetedCancellationClaimed = true;
            return true;
        }
    }

    public void PublishSubmittedCancellationWithoutNativeCompletion()
    {
        HidFeatureIoResult? result = null;
        lock (sync)
        {
            if (submissionState == SubmissionState.Submitted
                && !completionObserved
                && recordedTermination != HidFeatureTermination.Completed
                && !completion.Task.IsCompleted)
            {
                result = new HidFeatureIoResult(
                    Completion: null,
                    WasSubmitted: true,
                    Termination: recordedTermination);
            }
        }

        if (result is { } completed)
        {
            // The owning HID session treats this submitted cancellation as
            // indeterminate and poisons the handle. Native buffers remain pinned
            // until a late completion callback safely finalizes the operation.
            completion.TrySetResult(completed);
        }
    }

    public void RecordCompletion(NativeIoCompletion completion)
    {
        Finalization? finalization = null;
        lock (sync)
        {
            if (submissionState == SubmissionState.Finalized || completionObserved)
            {
                return;
            }

            nativeCompletion = completion;
            completionObserved = true;
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    private void RecordCancellationIntentLocked(HidFeatureTermination termination)
    {
        if (submissionState != SubmissionState.Finalized &&
            (!completionObserved || submissionState == SubmissionState.Submitting) &&
            recordedTermination == HidFeatureTermination.Completed)
        {
            recordedTermination = termination;
        }
    }

    private void ReleaseNativeUseLease()
    {
        Finalization? finalization;
        lock (sync)
        {
            nativeUseLeaseCount--;
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    private Finalization? TryBeginFinalizationLocked()
    {
        if (submissionState == SubmissionState.Finalized || nativeUseLeaseCount != 0)
        {
            return null;
        }

        bool canFinalize = abandonedBeforeSubmission ||
            (submissionState == SubmissionState.Submitted && completionObserved);
        if (!canFinalize)
        {
            return null;
        }

        bool wasSubmitted = !abandonedBeforeSubmission;
        var result = new HidFeatureIoResult(
            nativeCompletion,
            wasSubmitted,
            recordedTermination);
        submissionState = SubmissionState.Finalized;
        return new Finalization(result);
    }

    private void CompleteFinalization(Finalization? finalization)
    {
        if (finalization is null)
        {
            return;
        }

        try
        {
            finalizeResourcesAction();
            completion.TrySetResult(finalization.Value.Result);
        }
        catch (Exception exception)
        {
            completion.TrySetException(
                new NativeOperationFinalizationException(
                    finalization.Value.Result,
                    exception));
        }
    }

    private static void ValidateCancellationTermination(HidFeatureTermination termination)
    {
        if (termination == HidFeatureTermination.Completed)
        {
            throw new ArgumentOutOfRangeException(nameof(termination));
        }
    }

    private enum SubmissionState
    {
        Allocated,
        Submitting,
        Submitted,
        Finalized,
    }

    private readonly record struct Finalization(HidFeatureIoResult Result);
}
