using System.Runtime.InteropServices;

namespace OpenBlade.Protocol;

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
    private readonly object attachmentGate = new();
    private readonly Action<OverlappedFeatureOperationCheckpoint> checkpoint;
    private GCHandle pin;
    private ThreadPoolBoundHandle? originatingBoundHandle;
    private IWindowsHidKernel? kernel;
    private IntPtr nativeOverlapped;
    private bool attached;
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

    internal IntPtr PinnedAddress => pin.AddrOfPinnedObject();

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal bool IsOverlappedReleased => Volatile.Read(ref overlappedReleased) != 0;

    internal bool IsPinAllocated => Volatile.Read(ref pinReleased) == 0;

    internal int DisposeCount => Volatile.Read(ref disposeCount);

    internal void Attach(
        ThreadPoolBoundHandle boundHandle,
        IWindowsHidKernel nativeKernel,
        IntPtr overlapped)
    {
        ArgumentNullException.ThrowIfNull(boundHandle);
        ArgumentNullException.ThrowIfNull(nativeKernel);
        if (overlapped == IntPtr.Zero)
        {
            throw new ArgumentException(
                "The native overlapped pointer must be non-null.",
                nameof(overlapped));
        }

        lock (attachmentGate)
        {
            if (attached || IsDisposed)
            {
                throw new InvalidOperationException(
                    "A native overlapped allocation can be attached only once.");
            }

            originatingBoundHandle = boundHandle;
            kernel = nativeKernel;
            nativeOverlapped = overlapped;
            attached = true;
        }
    }

    internal IntPtr GetNativeOverlapped()
    {
        lock (attachmentGate)
        {
            if (!attached || nativeOverlapped == IntPtr.Zero)
            {
                throw new InvalidOperationException("No native overlapped allocation is attached.");
            }

            return nativeOverlapped;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref disposeCount);
        try
        {
            ReleaseOverlapped();
        }
        finally
        {
            ReleasePin();
        }
    }

    private void ReleaseOverlapped()
    {
        if (Interlocked.Exchange(ref overlappedReleased, 1) != 0)
        {
            return;
        }

        try
        {
            ThreadPoolBoundHandle? boundHandle;
            IWindowsHidKernel? nativeKernel;
            IntPtr overlapped;
            lock (attachmentGate)
            {
                boundHandle = originatingBoundHandle;
                nativeKernel = kernel;
                overlapped = nativeOverlapped;
            }

            if (boundHandle is not null && nativeKernel is not null && overlapped != IntPtr.Zero)
            {
                nativeKernel.FreeNativeOverlapped(boundHandle, overlapped);
            }
        }
        finally
        {
            NotifyCheckpoint(OverlappedFeatureOperationCheckpoint.OverlappedReleased);
        }
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

internal interface ICancellationRegistrationFactory
{
    IDisposable Register(CancellationToken cancellationToken, Action callback);
}

internal sealed class CancellationRegistrationFactory : ICancellationRegistrationFactory
{
    internal static CancellationRegistrationFactory Instance { get; } = new();

    private CancellationRegistrationFactory()
    {
    }

    public IDisposable Register(CancellationToken cancellationToken, Action callback) =>
        cancellationToken.Register(callback);
}

internal sealed class OverlappedFeatureOperation : IHidFeatureIo
{
    private readonly IOverlappedFeatureNative native;
    private readonly Action<OverlappedFeatureOperationCheckpoint> checkpoint;
    private readonly ICancellationRegistrationFactory registrationFactory;
    private readonly int featureReportLength;

    internal OverlappedFeatureOperation(IOverlappedFeatureNative native)
        : this(native, 91)
    {
    }

    internal OverlappedFeatureOperation(
        IOverlappedFeatureNative native,
        int featureReportLength)
        : this(
            native,
            featureReportLength,
            static _ => { },
            CancellationRegistrationFactory.Instance)
    {
    }

    internal OverlappedFeatureOperation(
        IOverlappedFeatureNative native,
        Action<OverlappedFeatureOperationCheckpoint> checkpoint)
        : this(native, 91, checkpoint, CancellationRegistrationFactory.Instance)
    {
    }

    internal OverlappedFeatureOperation(
        IOverlappedFeatureNative native,
        Action<OverlappedFeatureOperationCheckpoint> checkpoint,
        ICancellationRegistrationFactory registrationFactory)
        : this(native, 91, checkpoint, registrationFactory)
    {
    }

    internal OverlappedFeatureOperation(
        IOverlappedFeatureNative native,
        int featureReportLength,
        Action<OverlappedFeatureOperationCheckpoint> checkpoint,
        ICancellationRegistrationFactory registrationFactory)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(registrationFactory);
        if (featureReportLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(featureReportLength));
        }

        this.native = native;
        this.checkpoint = checkpoint;
        this.registrationFactory = registrationFactory;
        this.featureReportLength = featureReportLength;
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
        if (buffer.Length != featureReportLength)
        {
            throw new ArgumentException(
                $"Feature buffers must be exactly {featureReportLength} bytes.",
                nameof(buffer));
        }

        var operation = new NativeOperationToken(buffer, checkpoint);
        IDisposable? callerRegistration = null;
        IDisposable? deadlineRegistration = null;
        var state = new NativeOperationState(() =>
        {
            DisposeRegistration(
                callerRegistration,
                OverlappedFeatureOperationCheckpoint.CallerRegistrationDisposed);
            DisposeRegistration(
                deadlineRegistration,
                OverlappedFeatureOperationCheckpoint.DeadlineRegistrationDisposed);
            operation.Dispose();
        });

        try
        {
            callerRegistration = registrationFactory.Register(
                callerCancellation,
                () => HandleCancellation(
                    state,
                    handle,
                    operation,
                    HidFeatureTermination.CallerCanceled));
            deadlineRegistration = registrationFactory.Register(
                deadlineCancellation,
                () => HandleCancellation(
                    state,
                    handle,
                    operation,
                    HidFeatureTermination.DeadlineExpired));
        }
        catch
        {
            DisposeRegistration(
                callerRegistration,
                OverlappedFeatureOperationCheckpoint.CallerRegistrationDisposed);
            DisposeRegistration(
                deadlineRegistration,
                OverlappedFeatureOperationCheckpoint.DeadlineRegistrationDisposed);
            DisposeOperation(operation);
            throw;
        }

        if (!state.TryBeginSubmission(out NativeUseLease submissionLease))
        {
            DisposeRegistration(
                callerRegistration,
                OverlappedFeatureOperationCheckpoint.CallerRegistrationDisposed);
            DisposeRegistration(
                deadlineRegistration,
                OverlappedFeatureOperationCheckpoint.DeadlineRegistrationDisposed);
            DisposeOperation(operation);
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
                        NativeCancelResult cancelResult = native.Cancel(handle, operation);
                        if (cancelResult != NativeCancelResult.Requested)
                        {
                            state.PublishSubmittedCancellationWithoutNativeCompletion();
                        }
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

    private void DisposeRegistration(
        IDisposable? registration,
        OverlappedFeatureOperationCheckpoint disposedCheckpoint)
    {
        if (registration is null)
        {
            return;
        }

        try
        {
            registration.Dispose();
        }
        catch
        {
        }
        finally
        {
            NotifyCheckpoint(disposedCheckpoint);
        }
    }

    private static void DisposeOperation(NativeOperationToken operation)
    {
        try
        {
            operation.Dispose();
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
                NativeCancelResult cancelResult = native.Cancel(handle, operation);
                if (cancelResult != NativeCancelResult.Requested)
                {
                    state.PublishSubmittedCancellationWithoutNativeCompletion();
                }
            }
        }
        finally
        {
            cancellationLease.Dispose();
        }
    }
}
