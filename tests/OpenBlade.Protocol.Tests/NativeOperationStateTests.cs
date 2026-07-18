using System.Collections.Concurrent;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class NativeOperationStateTests
{
    [Fact]
    public async Task CompletionInsideSubmission_PreservesCancellationIntentAndSubmittedResult()
    {
        var finalizer = new FinalizerProbe();
        var state = new NativeOperationState(finalizer.FinalizeResources);
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        state.RecordCancellationIntent(HidFeatureTermination.CallerCanceled);
        Assert.False(state.TryAcquireCancellationLease(out _));
        Assert.False(state.TryClaimTargetedCancellation());

        var completionGate = new ScriptedGate();
        Task completion = Task.Run(async () =>
        {
            await completionGate.PauseAsync();
            state.RecordCompletion(new NativeIoCompletion(true, 64, 0));
        });

        await completionGate.WaitUntilPausedAsync();
        completionGate.Release();
        await completion;
        Assert.False(state.Completion.IsCompleted);

        state.RecordSubmitted();
        Assert.False(state.TryClaimTargetedCancellation());
        submissionLease.Dispose();

        HidFeatureIoResult result = await state.Completion;
        await completion;
        Assert.Equal(
            new HidFeatureIoResult(
                new NativeIoCompletion(true, 64, 0),
                true,
                HidFeatureTermination.CallerCanceled),
            result);
        Assert.Equal(1, finalizer.CallCount);
        Assert.False(state.TryAcquireCancellationLease(out _));
    }

    [Theory]
    [InlineData((int)HidFeatureTermination.CallerCanceled)]
    [InlineData((int)HidFeatureTermination.DeadlineExpired)]
    public async Task CancellationIntentAfterCompletionInsideSubmission_IsPreserved(
        int terminationValue)
    {
        var termination = (HidFeatureTermination)terminationValue;
        var finalizer = new FinalizerProbe();
        var state = new NativeOperationState(finalizer.FinalizeResources);
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        var completionGate = new ScriptedGate();
        var nativeCompletion = new NativeIoCompletion(true, 41, 0);
        Task completion = Task.Run(async () =>
        {
            await completionGate.PauseAsync();
            state.RecordCompletion(nativeCompletion);
        });

        await completionGate.WaitUntilPausedAsync();
        completionGate.Release();
        await completion;
        Assert.False(state.Completion.IsCompleted);

        state.RecordCancellationIntent(termination);
        state.RecordSubmitted();
        Assert.False(state.TryClaimTargetedCancellation());
        submissionLease.Dispose();

        Assert.Equal(
            new HidFeatureIoResult(nativeCompletion, true, termination),
            await state.Completion);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task CompletionAfterSubmissionBeforeCancellation_DeniesLateCancellation()
    {
        var finalizer = new FinalizerProbe();
        var state = CreateSubmittedState(finalizer, out NativeUseLease submissionLease);
        submissionLease.Dispose();

        var completionGate = new ScriptedGate();
        Task completion = Task.Run(async () =>
        {
            await completionGate.PauseAsync();
            state.RecordCompletion(new NativeIoCompletion(true, 17, 0));
        });

        await completionGate.WaitUntilPausedAsync();
        completionGate.Release();
        await completion;
        state.RecordCancellationIntent(HidFeatureTermination.DeadlineExpired);

        Assert.False(state.TryAcquireCancellationLease(out _));
        Assert.False(state.TryClaimTargetedCancellation());
        Assert.Equal(HidFeatureTermination.Completed, (await state.Completion).Termination);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Theory]
    [InlineData((int)HidFeatureTermination.CallerCanceled)]
    [InlineData((int)HidFeatureTermination.DeadlineExpired)]
    public async Task CancellationIntentAfterSubmittedCompletion_IsTooLateEvenWhileLeaseIsHeld(
        int terminationValue)
    {
        var termination = (HidFeatureTermination)terminationValue;
        var finalizer = new FinalizerProbe();
        var state = CreateSubmittedState(finalizer, out NativeUseLease submissionLease);
        var nativeCompletion = new NativeIoCompletion(true, 29, 0);

        state.RecordCompletion(nativeCompletion);
        Assert.False(state.Completion.IsCompleted);

        state.RecordCancellationIntent(termination);
        submissionLease.Dispose();

        Assert.Equal(
            new HidFeatureIoResult(
                nativeCompletion,
                true,
                HidFeatureTermination.Completed),
            await state.Completion);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task CompletionBetweenCancellationClaimAndNativeCancel_RemainsAliveThroughCancellationLease()
    {
        var finalizer = new FinalizerProbe();
        var state = CreateSubmittedState(finalizer, out NativeUseLease submissionLease);
        submissionLease.Dispose();
        state.RecordCancellationIntent(HidFeatureTermination.DeadlineExpired);
        Assert.True(state.TryAcquireCancellationLease(out NativeUseLease cancellationLease));
        Assert.True(state.TryClaimTargetedCancellation());

        var completionGate = new ScriptedGate();
        Task completion = Task.Run(async () =>
        {
            await completionGate.PauseAsync();
            state.RecordCompletion(new NativeIoCompletion(false, 0, 995));
        });

        await completionGate.WaitUntilPausedAsync();
        completionGate.Release();
        await completion;
        Assert.False(state.Completion.IsCompleted);
        Assert.Equal(0, finalizer.CallCount);
        Assert.False(state.TryClaimTargetedCancellation());

        cancellationLease.Dispose();

        HidFeatureIoResult result = await state.Completion;
        await completion;
        Assert.Equal(HidFeatureTermination.DeadlineExpired, result.Termination);
        Assert.True(result.WasSubmitted);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task TwoCancellers_CanHoldLeasesButOnlyOneClaimsTargetedCancellation()
    {
        var finalizer = new FinalizerProbe();
        var state = CreateSubmittedState(finalizer, out NativeUseLease submissionLease);
        submissionLease.Dispose();
        state.RecordCancellationIntent(HidFeatureTermination.CallerCanceled);
        var claims = new ConcurrentBag<bool>();
        var leases = new ConcurrentBag<NativeUseLease>();
        using var decisionBarrier = new Barrier(3);

        Task[] cancellers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            Assert.True(state.TryAcquireCancellationLease(out NativeUseLease lease));
            leases.Add(lease);
            decisionBarrier.SignalAndWait();
            claims.Add(state.TryClaimTargetedCancellation());
            decisionBarrier.SignalAndWait();
        })).ToArray();

        decisionBarrier.SignalAndWait();
        decisionBarrier.SignalAndWait();
        Assert.Single(claims, claim => claim);

        state.RecordCompletion(new NativeIoCompletion(false, 0, 995));
        Assert.False(state.Completion.IsCompleted);
        foreach (NativeUseLease lease in leases)
        {
            lease.Dispose();
        }

        await Task.WhenAll(cancellers);
        Assert.Equal(HidFeatureTermination.CallerCanceled, (await state.Completion).Termination);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task ImmediateFailure_FinalizesAfterSubmissionLeaseWithoutWaitingForCallback()
    {
        var finalizer = new FinalizerProbe();
        var state = new NativeOperationState(finalizer.FinalizeResources);
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        var failure = new NativeIoCompletion(false, 0, 87);

        state.RecordImmediateFailure(failure);

        Assert.False(state.Completion.IsCompleted);
        Assert.Equal(0, finalizer.CallCount);
        submissionLease.Dispose();

        Assert.Equal(
            new HidFeatureIoResult(failure, true, HidFeatureTermination.Completed),
            await state.Completion);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task FinalizerFailurePreservesTheCompletedNativeResult()
    {
        var cleanupFailure = new ArgumentException("cleanup");
        var state = new NativeOperationState(() => throw cleanupFailure);
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        var nativeFailure = new NativeIoCompletion(false, 0, 87);

        state.RecordImmediateFailure(nativeFailure);
        submissionLease.Dispose();

        NativeOperationFinalizationException error =
            await Assert.ThrowsAsync<NativeOperationFinalizationException>(
                async () => await state.Completion);
        Assert.Equal(
            new HidFeatureIoResult(
                nativeFailure,
                true,
                HidFeatureTermination.Completed),
            error.Result);
        Assert.Same(cleanupFailure, error.InnerException);
    }

    [Theory]
    [InlineData((int)HidFeatureTermination.CallerCanceled)]
    [InlineData((int)HidFeatureTermination.DeadlineExpired)]
    public async Task CancellationIntentAfterImmediateFailure_IsTooLateEvenWhileLeaseIsHeld(
        int terminationValue)
    {
        var termination = (HidFeatureTermination)terminationValue;
        var finalizer = new FinalizerProbe();
        var state = new NativeOperationState(finalizer.FinalizeResources);
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        var failure = new NativeIoCompletion(false, 0, 87);

        state.RecordImmediateFailure(failure);
        Assert.False(state.Completion.IsCompleted);

        state.RecordCancellationIntent(termination);
        submissionLease.Dispose();

        Assert.Equal(
            new HidFeatureIoResult(
                failure,
                true,
                HidFeatureTermination.Completed),
            await state.Completion);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task ImmediateFailureAfterSubmission_DoesNotReplaceTheEventualCompletion()
    {
        var finalizer = new FinalizerProbe();
        var state = CreateSubmittedState(finalizer, out NativeUseLease submissionLease);
        submissionLease.Dispose();
        var invalidImmediateFailure = new NativeIoCompletion(false, 0, 87);
        var eventualCompletion = new NativeIoCompletion(true, 23, 0);

        state.RecordImmediateFailure(invalidImmediateFailure);

        Assert.False(state.Completion.IsCompleted);
        Assert.Equal(0, finalizer.CallCount);

        state.RecordCompletion(eventualCompletion);

        Assert.Equal(eventualCompletion, (await state.Completion).Completion);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task Completion_WaitsForLastNativeUseLease()
    {
        var finalizer = new FinalizerProbe();
        var state = CreateSubmittedState(finalizer, out NativeUseLease submissionLease);
        state.RecordCancellationIntent(HidFeatureTermination.CallerCanceled);
        Assert.True(state.TryAcquireCancellationLease(out NativeUseLease firstCancellationLease));
        Assert.True(state.TryAcquireCancellationLease(out NativeUseLease secondCancellationLease));
        submissionLease.Dispose();

        state.RecordCompletion(new NativeIoCompletion(true, 32, 0));
        firstCancellationLease.Dispose();

        Assert.False(state.Completion.IsCompleted);
        Assert.Equal(0, finalizer.CallCount);
        secondCancellationLease.Dispose();

        Assert.True((await state.Completion).WasSubmitted);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public async Task FinalizationInProgress_AtomicallyDeniesNewLeases()
    {
        using var finalizerEntered = new Barrier(2);
        using var allowFinalizerReturn = new Barrier(2);
        var state = new NativeOperationState(() =>
        {
            finalizerEntered.SignalAndWait();
            allowFinalizerReturn.SignalAndWait();
        });
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        state.RecordSubmitted();
        submissionLease.Dispose();

        Task recordCompletion = Task.Run(() =>
            state.RecordCompletion(new NativeIoCompletion(true, 8, 0)));

        finalizerEntered.SignalAndWait();
        Assert.False(state.TryBeginSubmission(out _));
        Assert.False(state.TryAcquireCancellationLease(out _));
        allowFinalizerReturn.SignalAndWait();

        await recordCompletion;
        await state.Completion;
    }

    [Fact]
    public async Task DuplicateCompletionAndLeaseDisposal_FinalizeExactlyOnceWithFirstCompletion()
    {
        var finalizer = new FinalizerProbe();
        var state = CreateSubmittedState(finalizer, out NativeUseLease submissionLease);
        var first = new NativeIoCompletion(true, 9, 0);
        var duplicate = new NativeIoCompletion(false, 0, 123);

        state.RecordCompletion(first);
        state.RecordCompletion(duplicate);
        submissionLease.Dispose();
        submissionLease.Dispose();
        state.RecordCompletion(duplicate);

        Assert.Equal(first, (await state.Completion).Completion);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Theory]
    [InlineData((int)HidFeatureTermination.CallerCanceled)]
    [InlineData((int)HidFeatureTermination.DeadlineExpired)]
    public async Task AbandonBeforeSubmission_CompletesWithoutCompletionObservation(
        int terminationValue)
    {
        var termination = (HidFeatureTermination)terminationValue;
        var finalizer = new FinalizerProbe();
        var state = new NativeOperationState(finalizer.FinalizeResources);
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));

        state.AbandonBeforeSubmission(termination);

        Assert.False(state.Completion.IsCompleted);
        Assert.False(state.TryAcquireCancellationLease(out _));
        Assert.False(state.TryClaimTargetedCancellation());
        submissionLease.Dispose();

        Assert.Equal(new HidFeatureIoResult(null, false, termination), await state.Completion);
        Assert.Equal(1, finalizer.CallCount);
    }

    [Fact]
    public void AllocatedAndSubmittingStates_DenyCancellationAuthorization()
    {
        var state = new NativeOperationState(() => { });

        Assert.False(state.TryAcquireCancellationLease(out _));
        Assert.False(state.TryClaimTargetedCancellation());
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        Assert.False(state.TryBeginSubmission(out _));
        Assert.False(state.TryAcquireCancellationLease(out _));
        Assert.False(state.TryClaimTargetedCancellation());

        state.AbandonBeforeSubmission(HidFeatureTermination.CallerCanceled);
        submissionLease.Dispose();
    }

    [Fact]
    public void SubmittedWithoutCancellationIntent_DeniesCancellationAuthorization()
    {
        var state = new NativeOperationState(() => { });
        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));

        state.RecordSubmitted();

        Assert.False(state.TryAcquireCancellationLease(out _));
        Assert.False(state.TryClaimTargetedCancellation());

        state.RecordCompletion(new NativeIoCompletion(true, 1, 0));
        submissionLease.Dispose();
    }

    private static NativeOperationState CreateSubmittedState(
        FinalizerProbe finalizer,
        out NativeUseLease submissionLease)
    {
        var state = new NativeOperationState(finalizer.FinalizeResources);
        Assert.True(state.TryBeginSubmission(out submissionLease));
        state.RecordSubmitted();
        return state;
    }

    private sealed class FinalizerProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public void FinalizeResources() => Interlocked.Increment(ref _callCount);
    }

    private sealed class ScriptedGate
    {
        private readonly TaskCompletionSource _paused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilPausedAsync() => _paused.Task;

        public async Task PauseAsync()
        {
            _paused.TrySetResult();
            await _released.Task;
        }

        public void Release() => _released.TrySetResult();
    }
}
