using System.Runtime.InteropServices;

namespace OpenSynapse.Protocol;

internal interface IHidFeatureIo
{
    Task<HidFeatureIoResult> ExecuteAsync(
        OpenedHidHandle handle,
        uint controlCode,
        byte[] buffer,
        CancellationToken callerCancellation,
        CancellationToken deadlineCancellation);
}

internal enum NativeSubmissionKind
{
    Submitted,
    Pending,
    ImmediateFailure,
}

internal readonly record struct NativeSubmissionResult(
    NativeSubmissionKind Kind,
    int ErrorCode);

internal enum NativeCancelResult
{
    Requested,
    NotFound,
    Failed,
}

internal enum OverlappedFeatureOperationCheckpoint
{
    AfterRecordSubmitted,
    BeforeSubmissionLeaseRelease,
    CallerRegistrationDisposed,
    DeadlineRegistrationDisposed,
    OverlappedReleased,
    PinReleased,
    SubmissionLeaseReleased,
}

internal sealed class NativeOperationToken : IDisposable
{
    private readonly Action<OverlappedFeatureOperationCheckpoint> checkpoint;
    private GCHandle pin;
    private int disposed;
    private int disposeCount;
    private int overlappedReleased;
    private int pinReleased;

    internal NativeOperationToken(
        byte[] pinnedBuffer,
        Action<OverlappedFeatureOperationCheckpoint> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(pinnedBuffer);
        ArgumentNullException.ThrowIfNull(checkpoint);
        PinnedBuffer = pinnedBuffer;
        this.checkpoint = checkpoint;
        pin = GCHandle.Alloc(pinnedBuffer, GCHandleType.Pinned);
    }

    internal byte[] PinnedBuffer { get; }

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal bool IsOverlappedReleased => Volatile.Read(ref overlappedReleased) != 0;

    internal bool IsPinAllocated => Volatile.Read(ref pinReleased) == 0;

    internal int DisposeCount => Volatile.Read(ref disposeCount);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref disposeCount);
        ReleaseOverlapped();
        ReleasePin();
    }

    private void ReleaseOverlapped()
    {
        Interlocked.Exchange(ref overlappedReleased, 1);
        NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.OverlappedReleased);
    }

    private void ReleasePin()
    {
        if (Interlocked.Exchange(ref pinReleased, 1) == 0)
        {
            pin.Free();
            NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.PinReleased);
        }
    }

    private void NotifyCheckpoint(OverlappedFeatureOperationCheckpoint value)
    {
        try
        {
            checkpoint(value);
        }
        catch
        {
        }
    }
}

internal interface IOverlappedFeatureNative
{
    NativeSubmissionResult Submit(
        OpenedHidHandle handle,
        uint controlCode,
        byte[] buffer,
        NativeOperationToken operation,
        Action<NativeIoCompletion> completionCallback);

    NativeCancelResult Cancel(
        OpenedHidHandle handle,
        NativeOperationToken operation);
}

internal sealed class OverlappedFeatureOperation : IHidFeatureIo
{
    private readonly IOverlappedFeatureNative native;
    private readonly Action<OverlappedFeatureOperationCheckpoint> checkpoint;

    internal OverlappedFeatureOperation(IOverlappedFeatureNative native)
        : this(native, static _ => { })
    {
    }

    internal OverlappedFeatureOperation(
        IOverlappedFeatureNative native,
        Action<OverlappedFeatureOperationCheckpoint> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(checkpoint);
        this.native = native;
        this.checkpoint = checkpoint;
    }

    public Task<HidFeatureIoResult> ExecuteAsync(
        OpenedHidHandle handle,
        uint controlCode,
        byte[] buffer,
        CancellationToken callerCancellation,
        CancellationToken deadlineCancellation)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length != 91)
        {
            throw new ArgumentException("Feature buffers must be exactly 91 bytes.", nameof(buffer));
        }

        var operation = new NativeOperationToken(buffer, checkpoint);
        CancellationTokenRegistration callerRegistration = default;
        CancellationTokenRegistration deadlineRegistration = default;
        var state = new NativeOperationState(() =>
        {
            callerRegistration.Dispose();
            NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.CallerRegistrationDisposed);
            deadlineRegistration.Dispose();
            NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.DeadlineRegistrationDisposed);
            operation.Dispose();
        });

        callerRegistration = callerCancellation.Register(
            () => HandleCancellation(state, handle, operation, HidFeatureTermination.CallerCanceled));
        deadlineRegistration = deadlineCancellation.Register(
            () => HandleCancellation(state, handle, operation, HidFeatureTermination.DeadlineExpired));

        if (!state.TryBeginSubmission(out NativeUseLease submissionLease))
        {
            operation.Dispose();
            throw new InvalidOperationException("The operation could not begin submission.");
        }

        try
        {
            if (callerCancellation.IsCancellationRequested)
            {
                state.AbandonBeforeSubmission(HidFeatureTermination.CallerCanceled);
            }
            else if (deadlineCancellation.IsCancellationRequested)
            {
                state.AbandonBeforeSubmission(HidFeatureTermination.DeadlineExpired);
            }
            else
            {
                NativeSubmissionResult submission = native.Submit(
                    handle,
                    controlCode,
                    buffer,
                    operation,
                    state.RecordCompletion);

                if (submission.Kind == NativeSubmissionKind.ImmediateFailure)
                {
                    state.RecordImmediateFailure(
                        new NativeIoCompletion(false, 0, submission.ErrorCode));
                }
                else
                {
                    state.RecordSubmitted();
                    NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.AfterRecordSubmitted);
                    if (state.TryClaimTargetedCancellation())
                    {
                        _ = native.Cancel(handle, operation);
                    }
                }
            }
        }
        finally
        {
            NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.BeforeSubmissionLeaseRelease);
            submissionLease.Dispose();
            NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.SubmissionLeaseReleased);
        }

        return state.Completion;
    }

    private void NotifyCheckpoint(OverlappedFeatureOperationCheckpoint value)
    {
        try
        {
            checkpoint(value);
        }
        catch
        {
        }
    }

    private void HandleCancellation(
        NativeOperationState state,
        OpenedHidHandle handle,
        NativeOperationToken operation,
        HidFeatureTermination termination)
    {
        state.RecordCancellationIntent(termination);
        if (!state.TryAcquireCancellationLease(out NativeUseLease cancellationLease))
        {
            return;
        }

        try
        {
            if (state.TryClaimTargetedCancellation())
            {
                _ = native.Cancel(handle, operation);
            }
        }
        finally
        {
            cancellationLease.Dispose();
        }
    }
}
