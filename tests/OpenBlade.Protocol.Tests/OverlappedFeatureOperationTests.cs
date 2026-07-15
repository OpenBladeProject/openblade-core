using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class OverlappedFeatureOperationTests
{
    private static readonly NativeIoCompletion SuccessfulCompletion = new(true, 91, 0);
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(92)]
    public async Task ExecuteAsync_RejectsBuffersThatAreNotExactly91BytesWithoutShimCalls(
        int length)
    {
        var native = new ScriptedNative();
        var operation = new OverlappedFeatureOperation(native);

        await Assert.ThrowsAsync<ArgumentException>(() => operation.ExecuteAsync(
            CreateHandle(),
            0x1234,
            new byte[length],
            CancellationToken.None,
            CancellationToken.None));

        Assert.Equal(0, native.SubmitCalls);
        Assert.Equal(0, native.CancelCalls);
    }

    [Fact]
    public async Task ConstructorAndExecuteAsync_RejectNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new OverlappedFeatureOperation(null!));
        var operation = new OverlappedFeatureOperation(new ScriptedNative());

        await Assert.ThrowsAsync<ArgumentNullException>(() => operation.ExecuteAsync(
            null!, 1, new byte[91], CancellationToken.None, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => operation.ExecuteAsync(
            CreateHandle(), 1, null!, CancellationToken.None, CancellationToken.None));
    }

    [Fact]
    public async Task CallerRegistrationFailure_ReleasesTokenInOrderWithoutTouchingBorrowedHandle()
    {
        var setupException = new InvalidOperationException("caller registration setup failed");
        var registrationFactory = new ScriptedRegistrationFactory(
            failureCall: 1,
            setupException,
            events: null,
            throwOnDispose: false);
        var native = new ScriptedNative();
        var handle = new BorrowedHandleProbe();
        var checkpoints = new List<OverlappedFeatureOperationCheckpoint>();
        var operation = new OverlappedFeatureOperation(
            native,
            checkpoint =>
            {
                checkpoints.Add(checkpoint);
                throw new InvalidOperationException("cleanup observer failure");
            },
            registrationFactory);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operation.ExecuteAsync(
                handle.Handle,
                1,
                new byte[91],
                CancellationToken.None,
                CancellationToken.None));

        Assert.Same(setupException, exception);
        Assert.Empty(registrationFactory.Registrations);
        Assert.Equal(
            [
                OverlappedFeatureOperationCheckpoint.OverlappedReleased,
                OverlappedFeatureOperationCheckpoint.PinReleased,
            ],
            checkpoints);
        Assert.Equal(0, native.SubmitCalls);
        Assert.Equal(0, native.CancelCalls);
        Assert.False(handle.BoundDisposed);
        Assert.False(handle.FileDisposed);
    }

    [Fact]
    public async Task DeadlineRegistrationFailure_DisposesCallerThenTokenAndPreservesSetupException()
    {
        var setupException = new InvalidOperationException("deadline registration setup failed");
        var events = new List<string>();
        var registrationFactory = new ScriptedRegistrationFactory(
            failureCall: 2,
            setupException,
            events,
            throwOnDispose: true);
        var native = new ScriptedNative();
        var handle = new BorrowedHandleProbe();
        var checkpoints = new List<OverlappedFeatureOperationCheckpoint>();
        var operation = new OverlappedFeatureOperation(
            native,
            checkpoint =>
            {
                checkpoints.Add(checkpoint);
                events.Add(checkpoint.ToString());
            },
            registrationFactory);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operation.ExecuteAsync(
                handle.Handle,
                1,
                new byte[91],
                CancellationToken.None,
                CancellationToken.None));

        Assert.Same(setupException, exception);
        Assert.Equal(1, Assert.Single(registrationFactory.Registrations).DisposeCalls);
        Assert.Equal(
            [
                OverlappedFeatureOperationCheckpoint.CallerRegistrationDisposed,
                OverlappedFeatureOperationCheckpoint.OverlappedReleased,
                OverlappedFeatureOperationCheckpoint.PinReleased,
            ],
            checkpoints);
        Assert.Equal(
            [
                "RegistrationDisposed",
                "CallerRegistrationDisposed",
                "OverlappedReleased",
                "PinReleased",
            ],
            events);
        Assert.Equal(0, native.SubmitCalls);
        Assert.Equal(0, native.CancelCalls);
        Assert.False(handle.BoundDisposed);
        Assert.False(handle.FileDisposed);
    }

    [Theory]
    [InlineData(true, false, (int)HidFeatureTermination.CallerCanceled)]
    [InlineData(false, true, (int)HidFeatureTermination.DeadlineExpired)]
    [InlineData(true, true, (int)HidFeatureTermination.CallerCanceled)]
    public async Task PreCanceledTokens_AbandonBeforeSubmissionWithDistinctFirstIntent(
        bool cancelCaller,
        bool cancelDeadline,
        int expectedTerminationValue)
    {
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        if (cancelCaller)
        {
            caller.Cancel();
        }
        if (cancelDeadline)
        {
            deadline.Cancel();
        }
        var native = new ScriptedNative();

        HidFeatureIoResult result = await new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], caller.Token, deadline.Token);

        Assert.Equal(
            new HidFeatureIoResult(
                null,
                false,
                (HidFeatureTermination)expectedTerminationValue),
            result);
        Assert.Equal(0, native.SubmitCalls);
        Assert.Equal(0, native.CancelCalls);
    }

    [Fact]
    public async Task CancellationInsideSubmit_TargetsExactTokenOnceAfterSuccessfulReturn()
    {
        using var caller = new CancellationTokenSource();
        var native = new ScriptedNative
        {
            OnSubmit = context =>
            {
                caller.Cancel();
                return new NativeSubmissionResult(NativeSubmissionKind.Pending, 997);
            },
        };
        var buffer = new byte[91];
        OpenedHidHandle handle = CreateHandle();

        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            handle, 0x7788, buffer, caller.Token, CancellationToken.None);

        SubmitContext submission = Assert.Single(native.Submissions);
        CancelContext cancellation = Assert.Single(native.Cancellations);
        Assert.Same(handle, submission.Handle);
        Assert.Equal(0x7788u, submission.ControlCode);
        Assert.Same(buffer, submission.Buffer);
        Assert.Same(submission.Token, cancellation.Token);
        Assert.Same(handle, cancellation.Handle);
        Assert.False(execution.IsCompleted);
        Assert.False(submission.Token.IsDisposed);

        submission.Complete(new NativeIoCompletion(false, 0, 995));

        HidFeatureIoResult result = await execution;
        Assert.True(result.WasSubmitted);
        Assert.Equal(HidFeatureTermination.CallerCanceled, result.Termination);
        Assert.Equal(1, submission.Token.DisposeCount);
    }

    [Fact]
    public async Task CompletionThenCancellationInsideSubmit_RetainsCancellationWithoutCanceling()
    {
        using var caller = new CancellationTokenSource();
        var native = new ScriptedNative
        {
            OnSubmit = context =>
            {
                context.Complete(SuccessfulCompletion);
                caller.Cancel();
                return new NativeSubmissionResult(NativeSubmissionKind.Submitted, 0);
            },
        };

        HidFeatureIoResult result = await new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], caller.Token, CancellationToken.None);

        Assert.Equal(
            new HidFeatureIoResult(
                SuccessfulCompletion,
                true,
                HidFeatureTermination.CallerCanceled),
            result);
        Assert.Equal(0, native.CancelCalls);
        NativeOperationToken token = Assert.Single(native.Submissions).Token;
        Assert.Equal(1, token.DisposeCount);
    }

    [Fact]
    public async Task CompletionAfterRecordSubmittedBeforeCancelDecision_SkipsCancelAndRetainsToken()
    {
        using var caller = new CancellationTokenSource();
        SubmitContext submission = default;
        var native = new ScriptedNative
        {
            OnSubmit = context =>
            {
                submission = context;
                caller.Cancel();
                return new NativeSubmissionResult(NativeSubmissionKind.Submitted, 0);
            },
        };
        bool tokenAliveBeforeCompletion = false;
        bool tokenAliveAfterCompletion = false;
        var operation = new OverlappedFeatureOperation(native, checkpoint =>
        {
            if (checkpoint == OverlappedFeatureOperationCheckpoint.AfterRecordSubmitted)
            {
                tokenAliveBeforeCompletion = !submission.Token.IsDisposed;
                submission.Complete(SuccessfulCompletion);
                tokenAliveAfterCompletion = !submission.Token.IsDisposed;
            }
        });

        HidFeatureIoResult result = await operation.ExecuteAsync(
            CreateHandle(), 1, new byte[91], caller.Token, CancellationToken.None);

        Assert.Equal(HidFeatureTermination.CallerCanceled, result.Termination);
        Assert.True(result.WasSubmitted);
        Assert.Equal(0, native.CancelCalls);
        Assert.True(tokenAliveBeforeCompletion);
        Assert.True(tokenAliveAfterCompletion);
        Assert.Equal(1, submission.Token.DisposeCount);
    }

    [Theory]
    [InlineData((int)NativeSubmissionKind.Submitted, true)]
    [InlineData((int)NativeSubmissionKind.Submitted, false)]
    [InlineData((int)NativeSubmissionKind.Pending, true)]
    [InlineData((int)NativeSubmissionKind.Pending, false)]
    public async Task SubmittedAndPending_AwaitRealCallbackWithoutEarlyRelease(
        int kindValue,
        bool callbackInsideSubmit)
    {
        var kind = (NativeSubmissionKind)kindValue;
        var native = new ScriptedNative
        {
            OnSubmit = context =>
            {
                if (callbackInsideSubmit)
                {
                    context.Complete(SuccessfulCompletion);
                }
                return new NativeSubmissionResult(kind, kind == NativeSubmissionKind.Pending ? 997 : 0);
            },
        };

        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], CancellationToken.None, CancellationToken.None);
        SubmitContext submission = Assert.Single(native.Submissions);

        if (!callbackInsideSubmit)
        {
            Assert.False(execution.IsCompleted);
            Assert.False(submission.Token.IsDisposed);
            Assert.True(submission.Token.IsPinAllocated);
            submission.Complete(SuccessfulCompletion);
        }

        Assert.Equal(
            new HidFeatureIoResult(
                SuccessfulCompletion,
                true,
                HidFeatureTermination.Completed),
            await execution);
        Assert.Equal(1, submission.Token.DisposeCount);
    }

    [Fact]
    public async Task LaterCancellationRace_RetainsTokenUntilCancelReturnsNotFound()
    {
        using var caller = new CancellationTokenSource();
        using var cancelEntered = new ManualResetEventSlim();
        using var allowCancelReturn = new ManualResetEventSlim();
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.Pending, 997),
            OnCancel = context =>
            {
                cancelEntered.Set();
                Assert.True(allowCancelReturn.Wait(GateTimeout));
                Assert.False(context.Token.IsDisposed);
                Assert.True(context.Token.IsPinAllocated);
                return NativeCancelResult.NotFound;
            },
        };
        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], caller.Token, CancellationToken.None);
        SubmitContext submission = Assert.Single(native.Submissions);

        Task cancel = Task.Run(caller.Cancel);
        Assert.True(cancelEntered.Wait(GateTimeout));
        submission.Complete(new NativeIoCompletion(false, 0, 995));

        Assert.False(execution.IsCompleted);
        Assert.False(submission.Token.IsDisposed);
        allowCancelReturn.Set();
        await cancel;

        HidFeatureIoResult result = await execution;
        Assert.Equal(HidFeatureTermination.CallerCanceled, result.Termination);
        Assert.Equal(1, native.CancelCalls);
        Assert.Equal(1, submission.Token.DisposeCount);
    }

    [Fact]
    public async Task CallerAndDeadlineCallbacks_ContendButOnlyOneExactTokenCancelOccurs()
    {
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        using var cancelEntered = new ManualResetEventSlim();
        using var allowCancelReturn = new ManualResetEventSlim();
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.Pending, 997),
            OnCancel = _ =>
            {
                cancelEntered.Set();
                Assert.True(allowCancelReturn.Wait(GateTimeout));
                return NativeCancelResult.Requested;
            },
        };
        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], caller.Token, deadline.Token);
        SubmitContext submission = Assert.Single(native.Submissions);

        Task callerCancel = Task.Run(caller.Cancel);
        Assert.True(cancelEntered.Wait(GateTimeout));
        deadline.Cancel();

        Assert.Equal(1, native.CancelCalls);
        Assert.Same(submission.Token, Assert.Single(native.Cancellations).Token);
        submission.Complete(new NativeIoCompletion(false, 0, 995));
        Assert.False(execution.IsCompleted);
        allowCancelReturn.Set();
        await callerCancel;

        Assert.Equal(HidFeatureTermination.CallerCanceled, (await execution).Termination);
        Assert.Equal(1, submission.Token.DisposeCount);
    }

    [Fact]
    public async Task ImmediateFailure_UsesNativeFailureWithoutCallbackOrCancellation()
    {
        using var caller = new CancellationTokenSource();
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.ImmediateFailure, 87),
        };

        HidFeatureIoResult result = await new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], caller.Token, CancellationToken.None);
        caller.Cancel();

        Assert.Equal(
            new HidFeatureIoResult(
                new NativeIoCompletion(false, 0, 87),
                true,
                HidFeatureTermination.Completed),
            result);
        Assert.Equal(0, native.CancelCalls);
        Assert.Equal(1, Assert.Single(native.Submissions).Token.DisposeCount);
    }

    [Fact]
    public async Task ImmediateFailure_FinalizationOccursDuringSubmissionLeaseRelease()
    {
        var checkpoints = new List<OverlappedFeatureOperationCheckpoint>();
        NativeOperationToken? token = null;
        var native = new ScriptedNative
        {
            OnSubmit = context =>
            {
                token = context.Token;
                return new NativeSubmissionResult(NativeSubmissionKind.ImmediateFailure, 87);
            },
        };
        bool tokenAliveBeforeLeaseRelease = false;
        bool pinAllocatedBeforeLeaseRelease = false;
        var operation = new OverlappedFeatureOperation(native, checkpoint =>
        {
            checkpoints.Add(checkpoint);
            if (checkpoint == OverlappedFeatureOperationCheckpoint.BeforeSubmissionLeaseRelease)
            {
                tokenAliveBeforeLeaseRelease = token is { IsDisposed: false };
                pinAllocatedBeforeLeaseRelease = token is { IsPinAllocated: true };
            }
        });

        await operation.ExecuteAsync(
            CreateHandle(), 1, new byte[91], CancellationToken.None, CancellationToken.None);

        Assert.NotNull(token);
        Assert.True(tokenAliveBeforeLeaseRelease);
        Assert.True(pinAllocatedBeforeLeaseRelease);
        Assert.Equal(
            [
                OverlappedFeatureOperationCheckpoint.BeforeSubmissionLeaseRelease,
                OverlappedFeatureOperationCheckpoint.CallerRegistrationDisposed,
                OverlappedFeatureOperationCheckpoint.DeadlineRegistrationDisposed,
                OverlappedFeatureOperationCheckpoint.OverlappedReleased,
                OverlappedFeatureOperationCheckpoint.PinReleased,
                OverlappedFeatureOperationCheckpoint.SubmissionLeaseReleased,
            ],
            checkpoints);
    }

    [Fact]
    public async Task ThrowingCheckpointObserver_CannotInterruptLeaseOrTokenCleanup()
    {
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.ImmediateFailure, 87),
        };
        var operation = new OverlappedFeatureOperation(
            native,
            _ => throw new InvalidOperationException("diagnostic observer failure"));

        HidFeatureIoResult result = await operation.ExecuteAsync(
            CreateHandle(), 1, new byte[91], CancellationToken.None, CancellationToken.None);

        Assert.Equal(87, result.Completion?.ErrorCode);
        NativeOperationToken token = Assert.Single(native.Submissions).Token;
        Assert.True(token.IsDisposed);
        Assert.True(token.IsOverlappedReleased);
        Assert.False(token.IsPinAllocated);
    }

    [Fact]
    public async Task Submit_ReceivesAndTokenPinsSameExact91ByteObjectWithUniqueTokenPerCall()
    {
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.ImmediateFailure, 87),
        };
        var first = new byte[91];
        var second = new byte[91];
        var operation = new OverlappedFeatureOperation(native);

        await operation.ExecuteAsync(
            CreateHandle(), 1, first, CancellationToken.None, CancellationToken.None);
        await operation.ExecuteAsync(
            CreateHandle(), 1, second, CancellationToken.None, CancellationToken.None);

        SubmitContext[] submissions = native.Submissions.ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.Same(first, submissions[0].Buffer);
        Assert.Same(first, submissions[0].Token.PinnedBuffer);
        Assert.Equal(91, submissions[0].Token.PinnedBuffer.Length);
        Assert.Same(second, submissions[1].Buffer);
        Assert.Same(second, submissions[1].Token.PinnedBuffer);
        Assert.NotSame(submissions[0].Token, submissions[1].Token);
    }

    [Fact]
    public async Task DuplicateAndLateCompletion_PreservesFirstCompletionAndFinalizesOnce()
    {
        var first = new NativeIoCompletion(true, 25, 0);
        var duplicate = new NativeIoCompletion(false, 0, 123);
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.Pending, 997),
        };
        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], CancellationToken.None, CancellationToken.None);
        SubmitContext submission = Assert.Single(native.Submissions);

        submission.Complete(first);
        submission.Complete(duplicate);
        HidFeatureIoResult result = await execution;
        submission.Complete(duplicate);

        Assert.Equal(first, result.Completion);
        Assert.Equal(1, submission.Token.DisposeCount);
        submission.Token.Dispose();
        Assert.Equal(1, submission.Token.DisposeCount);
    }

    [Fact]
    public async Task CompletionSettlesOnlyAfterTokenOverlappedAndPinAreReleased()
    {
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.Pending, 997),
        };
        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 1, new byte[91], caller.Token, deadline.Token);
        SubmitContext submission = Assert.Single(native.Submissions);

        submission.Complete(SuccessfulCompletion);
        await execution;

        Assert.True(submission.Token.IsOverlappedReleased);
        Assert.False(submission.Token.IsPinAllocated);
        Assert.True(submission.Token.IsDisposed);
        caller.Cancel();
        deadline.Cancel();
        Assert.Equal(0, native.CancelCalls);
    }

    [Fact]
    public async Task FinalizerDisposesRegistrationsThenOverlappedThenPinBeforeCompletionSettles()
    {
        var checkpoints = new ConcurrentQueue<string>();
        var native = new ScriptedNative
        {
            OnSubmit = _ => new NativeSubmissionResult(NativeSubmissionKind.Pending, 997),
        };
        var operation = new OverlappedFeatureOperation(
            native,
            checkpoint => checkpoints.Enqueue(checkpoint.ToString()));
        Task<HidFeatureIoResult> execution = operation.ExecuteAsync(
            CreateHandle(), 1, new byte[91], CancellationToken.None, CancellationToken.None);
        SubmitContext submission = Assert.Single(native.Submissions);

        submission.Complete(SuccessfulCompletion);
        await execution;
        checkpoints.Enqueue("CompletionObservedByCaller");

        string[] ordered = checkpoints.ToArray();
        Assert.True(
            Array.IndexOf(ordered, "CallerRegistrationDisposed") <
            Array.IndexOf(ordered, "DeadlineRegistrationDisposed"));
        Assert.True(
            Array.IndexOf(ordered, "DeadlineRegistrationDisposed") <
            Array.IndexOf(ordered, "OverlappedReleased"));
        Assert.True(
            Array.IndexOf(ordered, "OverlappedReleased") <
            Array.IndexOf(ordered, "PinReleased"));
        Assert.True(
            Array.IndexOf(ordered, "PinReleased") <
            Array.IndexOf(ordered, "CompletionObservedByCaller"));
    }

    private static OpenedHidHandle CreateHandle() =>
        (OpenedHidHandle)RuntimeHelpers.GetUninitializedObject(typeof(OpenedHidHandle));

    private sealed class BorrowedHandleProbe
    {
        public BorrowedHandleProbe()
        {
            var fileHandle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
            var boundHandle = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(
                typeof(ThreadPoolBoundHandle));
            Handle = new OpenedHidHandle(
                fileHandle,
                boundHandle,
                () => BoundDisposed = true,
                () => FileDisposed = true);
        }

        public OpenedHidHandle Handle { get; }

        public bool BoundDisposed { get; private set; }

        public bool FileDisposed { get; private set; }
    }

    private sealed class ScriptedRegistrationFactory : ICancellationRegistrationFactory
    {
        private readonly int failureCall;
        private readonly Exception setupException;
        private readonly List<string>? events;
        private readonly bool throwOnDispose;
        private int calls;

        public ScriptedRegistrationFactory(
            int failureCall,
            Exception setupException,
            List<string>? events,
            bool throwOnDispose)
        {
            this.failureCall = failureCall;
            this.setupException = setupException;
            this.events = events;
            this.throwOnDispose = throwOnDispose;
        }

        public List<RegistrationProbe> Registrations { get; } = [];

        public IDisposable Register(CancellationToken cancellationToken, Action callback)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == failureCall)
            {
                throw setupException;
            }

            var registration = new RegistrationProbe(events, throwOnDispose);
            Registrations.Add(registration);
            return registration;
        }
    }

    private sealed class RegistrationProbe : IDisposable
    {
        private readonly List<string>? events;
        private readonly bool throwOnDispose;

        public RegistrationProbe(List<string>? events, bool throwOnDispose)
        {
            this.events = events;
            this.throwOnDispose = throwOnDispose;
        }

        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            events?.Add("RegistrationDisposed");
            if (throwOnDispose)
            {
                throw new InvalidOperationException("registration cleanup failed");
            }
        }
    }

    private readonly record struct SubmitContext(
        OpenedHidHandle Handle,
        uint ControlCode,
        byte[] Buffer,
        NativeOperationToken Token,
        Action<NativeIoCompletion> Complete);

    private readonly record struct CancelContext(
        OpenedHidHandle Handle,
        NativeOperationToken Token);

    private sealed class ScriptedNative : IOverlappedFeatureNative
    {
        private int submitCalls;
        private int cancelCalls;

        public Func<SubmitContext, NativeSubmissionResult>? OnSubmit { get; init; }

        public Func<CancelContext, NativeCancelResult>? OnCancel { get; init; }

        public ConcurrentQueue<SubmitContext> Submissions { get; } = new();

        public ConcurrentQueue<CancelContext> Cancellations { get; } = new();

        public int SubmitCalls => Volatile.Read(ref submitCalls);

        public int CancelCalls => Volatile.Read(ref cancelCalls);

        public NativeSubmissionResult Submit(
            OpenedHidHandle handle,
            uint controlCode,
            byte[] buffer,
            NativeOperationToken operation,
            Action<NativeIoCompletion> completionCallback)
        {
            Interlocked.Increment(ref submitCalls);
            var context = new SubmitContext(
                handle,
                controlCode,
                buffer,
                operation,
                completionCallback);
            Submissions.Enqueue(context);
            return OnSubmit?.Invoke(context) ??
                new NativeSubmissionResult(NativeSubmissionKind.ImmediateFailure, 87);
        }

        public NativeCancelResult Cancel(
            OpenedHidHandle handle,
            NativeOperationToken operation)
        {
            Interlocked.Increment(ref cancelCalls);
            var context = new CancelContext(handle, operation);
            Cancellations.Enqueue(context);
            return OnCancel?.Invoke(context) ?? NativeCancelResult.Requested;
        }
    }
}
