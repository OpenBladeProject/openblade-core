namespace OpenSynapse.Protocol;

internal sealed class NativeUseLease : IDisposable
{
    private Action? _release;

    internal NativeUseLease(Action release)
    {
        _release = release;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed class NativeOperationState
{
    private readonly object _sync = new();
    private readonly Action _finalizeResources;
    private readonly TaskCompletionSource<HidFeatureIoResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SubmissionState _submissionState = SubmissionState.Allocated;
    private NativeIoCompletion? _nativeCompletion;
    private HidFeatureTermination _termination = HidFeatureTermination.Completed;
    private int _nativeUseLeaseCount;
    private bool _completionObserved;
    private bool _abandonedBeforeSubmission;
    private bool _targetedCancellationClaimed;

    public NativeOperationState(Action finalizeResources)
    {
        ArgumentNullException.ThrowIfNull(finalizeResources);
        _finalizeResources = finalizeResources;
    }

    public Task<HidFeatureIoResult> Completion => _completion.Task;

    public bool TryBeginSubmission(out NativeUseLease lease)
    {
        lock (_sync)
        {
            if (_submissionState != SubmissionState.Allocated)
            {
                lease = null!;
                return false;
            }

            _submissionState = SubmissionState.Submitting;
            _nativeUseLeaseCount++;
            lease = new NativeUseLease(ReleaseNativeUseLease);
            return true;
        }
    }

    public void RecordSubmitted()
    {
        Finalization? finalization = null;
        lock (_sync)
        {
            if (_submissionState != SubmissionState.Submitting || _abandonedBeforeSubmission)
            {
                return;
            }

            _submissionState = SubmissionState.Submitted;
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    public void AbandonBeforeSubmission(HidFeatureTermination termination)
    {
        ValidateCancellationTermination(termination);
        Finalization? finalization = null;
        lock (_sync)
        {
            if (_submissionState is SubmissionState.Submitted or SubmissionState.Finalized)
            {
                return;
            }

            _abandonedBeforeSubmission = true;
            RecordCancellationIntentLocked(termination);
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    public void RecordImmediateFailure(NativeIoCompletion failure)
    {
        Finalization? finalization = null;
        lock (_sync)
        {
            if (_submissionState != SubmissionState.Submitting || _abandonedBeforeSubmission)
            {
                return;
            }

            _submissionState = SubmissionState.Submitted;
            if (!_completionObserved)
            {
                _nativeCompletion = failure;
                _completionObserved = true;
            }

            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    public void RecordCancellationIntent(HidFeatureTermination termination)
    {
        ValidateCancellationTermination(termination);
        lock (_sync)
        {
            RecordCancellationIntentLocked(termination);
        }
    }

    public bool TryAcquireCancellationLease(out NativeUseLease lease)
    {
        lock (_sync)
        {
            if (_submissionState != SubmissionState.Submitted ||
                _completionObserved ||
                _termination == HidFeatureTermination.Completed)
            {
                lease = null!;
                return false;
            }

            _nativeUseLeaseCount++;
            lease = new NativeUseLease(ReleaseNativeUseLease);
            return true;
        }
    }

    public bool TryClaimTargetedCancellation()
    {
        lock (_sync)
        {
            if (_submissionState != SubmissionState.Submitted ||
                _completionObserved ||
                _termination == HidFeatureTermination.Completed ||
                _targetedCancellationClaimed)
            {
                return false;
            }

            _targetedCancellationClaimed = true;
            return true;
        }
    }

    public void RecordCompletion(NativeIoCompletion completion)
    {
        Finalization? finalization = null;
        lock (_sync)
        {
            if (_submissionState == SubmissionState.Finalized || _completionObserved)
            {
                return;
            }

            _nativeCompletion = completion;
            _completionObserved = true;
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    private void RecordCancellationIntentLocked(HidFeatureTermination termination)
    {
        if (_submissionState != SubmissionState.Finalized &&
            (!_completionObserved || _submissionState == SubmissionState.Submitting) &&
            _termination == HidFeatureTermination.Completed)
        {
            _termination = termination;
        }
    }

    private void ReleaseNativeUseLease()
    {
        Finalization? finalization;
        lock (_sync)
        {
            _nativeUseLeaseCount--;
            finalization = TryBeginFinalizationLocked();
        }

        CompleteFinalization(finalization);
    }

    private Finalization? TryBeginFinalizationLocked()
    {
        if (_submissionState == SubmissionState.Finalized || _nativeUseLeaseCount != 0)
        {
            return null;
        }

        bool canFinalize = _abandonedBeforeSubmission ||
            (_submissionState == SubmissionState.Submitted && _completionObserved);
        if (!canFinalize)
        {
            return null;
        }

        bool wasSubmitted = !_abandonedBeforeSubmission;
        var result = new HidFeatureIoResult(
            _nativeCompletion,
            wasSubmitted,
            _termination);
        _submissionState = SubmissionState.Finalized;
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
            _finalizeResources();
            _completion.TrySetResult(finalization.Value.Result);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
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
