using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRazerHidSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task VariableEffectResponse_RequiresValidatorAndRetainsExactEnvelopeChecks()
    {
        var io = new ScriptedIo(SetSuccess(), GetSuccess(1, 0x0F, 0x82, [1, 5, 3, 0, 0, 0]));
        await using var actor = CreateActor(io);
        var command = new RazerCommandSpec(0x0F, 0x82, new byte[] { 1, 5, 0 }, 3,
            payload => payload.Length == 6 && payload.Span[0] == 1 && payload.Span[1] == 5)
        { AllowVariableResponseLength = true };
        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(command,
            () => TransactionIdAssignment.Valid(1), Timeout, CancellationToken.None);
        Assert.Null(result.Failure);
        Assert.Equal(6, result.Value.Length);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
    }

    [Theory]
    [InlineData(false, HidProtocolValidationFailure.PayloadLength)]
    [InlineData(true, HidProtocolValidationFailure.PayloadValidator)]
    public async Task VariableResponse_DefaultRemainsExactAndSemanticRejectionPoisons(bool variable, HidProtocolValidationFailure expected)
    {
        var io = new ScriptedIo(SetSuccess(), GetSuccess(1, 0x0F, 0x82, [1, 6, 3, 0]));
        await using var actor = CreateActor(io);
        var command = new RazerCommandSpec(0x0F, 0x82, new byte[] { 1, 5, 0 }, 3,
            payload => payload.Span[1] == 5)
        { AllowVariableResponseLength = variable };
        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(command,
            () => TransactionIdAssignment.Valid(1), Timeout, CancellationToken.None);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(expected, result.Diagnostic?.ProtocolValidationFailure);
        Assert.NotEqual(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        HidSessionResult<ReadOnlyMemory<byte>> subsequent = await actor.ExecuteAsync(command,
            () => TransactionIdAssignment.Valid(2), Timeout, CancellationToken.None);
        Assert.Contains(subsequent.Failure, new HidSessionFailureKind?[]
        {
            HidSessionFailureKind.Poisoned, HidSessionFailureKind.Disposed
        });
        Assert.Equal(2, io.Calls.Count);
    }

    [Fact]
    public async Task VariableResponse_WithoutValidatorIsRejectedBeforeNativeIo()
    {
        var io = new ScriptedIo();
        await using var actor = CreateActor(io);
        var command = new RazerCommandSpec(0x0F, 0x82, new byte[] { 1, 5, 0 }, 3)
        { AllowVariableResponseLength = true };
        Assert.Throws<ArgumentException>(() =>
        {
            _ = actor.ExecuteAsync(command, () => TransactionIdAssignment.Valid(1), Timeout, CancellationToken.None);
        });
        Assert.Empty(io.Calls);
    }

    [Fact]
    public async Task NewActorReportsHealthyDisposition()
    {
        IRazerCommandSession actor = CreateActor(new ScriptedIo());

        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);

        await actor.DisposeAsync();
    }

    [Fact]
    public async Task HealthyPreSubmissionFailuresRemainHealthy()
    {
        using var canceled = new CancellationTokenSource();
        var io = new ScriptedIo();
        IRazerCommandSession actor = CreateActor(io, new ScriptedGuard(GuardKind.Conflict));

        Assert.Equal(
            HidSessionFailureKind.Canceled,
            (await actor.ExecuteAsync(
                new(1, 1, [], 0),
                () =>
                {
                    canceled.Cancel();
                    return TransactionIdAssignment.Valid(1);
                },
                Timeout,
                canceled.Token)).Failure);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        Assert.Equal(
            HidSessionFailureKind.ConflictBlocked,
            (await actor.ExecuteAsync(
                new(1, 2, [], 0),
                () => TransactionIdAssignment.Valid(2),
                Timeout,
                default)).Failure);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        Assert.Empty(io.Calls);

        await actor.DisposeAsync();
    }

    [Fact]
    public async Task PoisonDispositionIsVisibleBeforeCommandContinuationRuns()
    {
        using var releaseDisposal = new ManualResetEventSlim();
        var continuationDisposition = new TaskCompletionSource<RazerCommandSessionDisposition>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(Result(new HidFeatureIoResult(
            new NativeIoCompletion(false, 0, 5),
            true,
            HidFeatureTermination.Completed)));
        IRazerCommandSession actor = CreateActor(io, disposeBound: releaseDisposal.Wait);

        Task<HidSessionResult<ReadOnlyMemory<byte>>> command = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);
        _ = command.ContinueWith(
            _ => continuationDisposition.TrySetResult(actor.Disposition),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            Assert.Equal(HidSessionFailureKind.NativeIoFailure, (await command).Failure);
            Assert.Equal(
                RazerCommandSessionDisposition.RecoveryRequired,
                await continuationDisposition.Task);
        }
        finally
        {
            releaseDisposal.Set();
            await actor.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeReportsDisposingUntilIoHandleAndLeaseCleanupFinish()
    {
        var gate = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var context = new ValidationRunContext();
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease lease));
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        var handle = new OpenedHidHandle(
            file,
            bound,
            () => events.Add("bound"),
            () =>
            {
                events.Add("file");
                Assert.False(context.TryAcquireActor(out _));
            });
        var io = new ScriptedIo((_, _, _, _, _) => gate.Task);
        IRazerCommandSession actor = new WindowsRazerHidSession(
            handle,
            io,
            new ScriptedGuard(),
            TimeProvider.System,
            lease,
            ControllerExclusivityPolicy.Strict);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> active = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);
        await io.WaitForCallsAsync(1);

        Task disposal = actor.DisposeAsync().AsTask();
        Assert.Equal(RazerCommandSessionDisposition.Disposing, actor.Disposition);
        Assert.False(disposal.IsCompleted);
        Assert.Empty(events);

        gate.SetResult(new HidFeatureIoResult(null, false, HidFeatureTermination.CallerCanceled));
        Assert.Equal(HidSessionFailureKind.Canceled, (await active).Failure);
        await disposal;

        Assert.Equal(["bound", "file"], events);
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, actor.Disposition);
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease replacement));
        replacement.Dispose();
    }

    [Fact]
    public async Task DisposeAfterPoisonTransitionsThroughDisposingToDisposed()
    {
        var enteredDisposal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDisposal = new ManualResetEventSlim();
        var io = new ScriptedIo(Result(new HidFeatureIoResult(
            new NativeIoCompletion(false, 0, 5),
            true,
            HidFeatureTermination.Completed)));
        IRazerCommandSession actor = CreateActor(
            io,
            disposeBound: () =>
            {
                enteredDisposal.TrySetResult(true);
                releaseDisposal.Wait();
            });

        Assert.Equal(
            HidSessionFailureKind.NativeIoFailure,
            (await actor.ExecuteAsync(
                new(1, 1, [], 0),
                () => TransactionIdAssignment.Valid(1),
                Timeout,
                default)).Failure);
        Assert.Equal(RazerCommandSessionDisposition.RecoveryRequired, actor.Disposition);

        Task disposal = actor.DisposeAsync().AsTask();
        await enteredDisposal.Task;
        Assert.Equal(RazerCommandSessionDisposition.Disposing, actor.Disposition);

        releaseDisposal.Set();
        await disposal;
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, actor.Disposition);
    }

    [Fact]
    public async Task HandleDisposalFaultStillReleasesLeaseAndReportsDisposed()
    {
        var context = new ValidationRunContext();
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease lease));
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        var handle = new OpenedHidHandle(
            file,
            bound,
            () => throw new IOException("close"),
            () => Assert.False(context.TryAcquireActor(out _)));
        IRazerCommandSession actor = new WindowsRazerHidSession(
            handle,
            new ScriptedIo(),
            new ScriptedGuard(),
            TimeProvider.System,
            lease,
            ControllerExclusivityPolicy.Strict);

        Task disposal = actor.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<IOException>(() => disposal);

        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, actor.Disposition);
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease replacement));
        replacement.Dispose();
    }

    [Fact]
    public async Task RepeatDisposeSharesOneCleanupTaskAndPath()
    {
        var events = new List<string>();
        IRazerCommandSession actor = CreateActor(new ScriptedIo(), events: events);

        Task first = actor.DisposeAsync().AsTask();
        Task second = actor.DisposeAsync().AsTask();

        Assert.Same(first, second);
        await Task.WhenAll(first, second);
        Assert.Equal(["bound", "file"], events);
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, actor.Disposition);
    }

    [Fact]
    public async Task ExecuteAsync_SerializesFifoAndInvokesAssignmentsExactlyOnce()
    {
        var firstGate = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(
            (_, _, _, _, _) => firstGate.Task,
            GetSuccess(1, 2, 3, [0x11]),
            SetSuccess(),
            GetSuccess(2, 2, 4, [0x22]));
        await using WindowsRazerHidSession actor = CreateActor(io);
        int assignments = 0;

        Task<HidSessionResult<ReadOnlyMemory<byte>>> first = actor.ExecuteAsync(
            new(2, 3, [0xAA], 1), () => { assignments++; return TransactionIdAssignment.Valid(1); }, Timeout, default);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> second = actor.ExecuteAsync(
            new(2, 4, [], 1), () => { assignments++; return TransactionIdAssignment.Valid(2); }, Timeout, default);

        await io.WaitForCallsAsync(1);
        Assert.Equal(1, assignments);
        Assert.Single(io.Calls);
        firstGate.SetResult(SetSuccessResult());
        Assert.Equal([0x11], (await first).Value.ToArray());
        Assert.Equal([0x22], (await second).Value.ToArray());
        Assert.Equal(2, assignments);
        Assert.Equal([SetCode, GetCode, SetCode, GetCode], io.Calls.Select(c => c.ControlCode));
    }

    [Fact]
    public async Task SubmitAsync_CompletesAfterSetWithoutSettleOrGet()
    {
        var time = new ManualTimeProvider();
        var io = new ScriptedIo(SetSuccess());
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            time: time,
            featureReportSettleDelay: TimeSpan.FromSeconds(1));

        HidSessionResult<ReadOnlyMemory<byte>> result =
            await ((IRazerSetFeatureCommandSession)actor).SubmitAsync(
                new(0x03, 0x0B, [0xFF, 0x00], 2),
                () => TransactionIdAssignment.Valid(1),
                Timeout,
                default);

        Assert.Null(result.Failure);
        Assert.True(result.Value.IsEmpty);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        Assert.Equal([SetCode], io.Calls.Select(call => call.ControlCode));
    }

    [Fact]
    public async Task SubmitBatchAsync_UsesOneDeadlineAndBufferInStrictCommandOrder()
    {
        var observed = new List<(byte Transaction, byte ClassId, byte CommandId, string Arguments)>();
        var io = new ScriptedIo(
            Enumerable.Range(0, 3)
                .Select(_ =>
                    (Func<OpenedHidHandle, uint, byte[], CancellationToken,
                        CancellationToken, Task<HidFeatureIoResult>>)
                    ((_, _, buffer, _, _) =>
                    {
                        int length = buffer[6];
                        observed.Add((
                            buffer[2],
                            buffer[7],
                            buffer[8],
                            Convert.ToHexString(buffer.AsSpan(9, length))));
                        return Task.FromResult(SetSuccessResult());
                    }))
                .ToArray());
        await using WindowsRazerHidSession actor = CreateActor(io);
        var commands = new RazerCommandSpec[]
        {
            new(0x03, 0x0B, [0xFF, 0x00], 2),
            new(0x03, 0x0B, [0xFF, 0x01], 2),
            new(0x03, 0x0A, [0x05, 0x00], 2),
        };
        int transaction = 0;

        HidSessionResult<ReadOnlyMemory<byte>> result =
            await ((IRazerSetFeatureCommandSession)actor).SubmitBatchAsync(
                commands,
                () => TransactionIdAssignment.Valid(
                    checked((byte)Interlocked.Increment(ref transaction))),
                Timeout,
                default);

        Assert.Null(result.Failure);
        Assert.True(result.Value.IsEmpty);
        Assert.Equal(
            [
                (1, 0x03, 0x0B, "FF00"),
                (2, 0x03, 0x0B, "FF01"),
                (3, 0x03, 0x0A, "0500"),
            ],
            observed.Select(value => (
                (int)value.Transaction,
                (int)value.ClassId,
                (int)value.CommandId,
                value.Arguments)));
        Assert.Equal([SetCode, SetCode, SetCode], io.Calls.Select(call => call.ControlCode));
        Assert.All(
            io.Calls,
            call => Assert.Equal(io.Calls[0].Deadline, call.Deadline));
        Assert.All(
            io.Calls,
            call => Assert.Same(io.Calls[0].Buffer, call.Buffer));
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
    }

    [Fact]
    public async Task SubmitBatchAsync_CancellationAfterFirstWritePoisonsAndDrainsQueue()
    {
        using var caller = new CancellationTokenSource();
        var firstCompletion =
            new TaskCompletionSource<HidFeatureIoResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(
            (_, _, _, _, _) => firstCompletion.Task,
            SetSuccess());
        await using WindowsRazerHidSession actor = CreateActor(io);
        int batchAssignments = 0;
        int queuedAssignments = 0;

        Task<HidSessionResult<ReadOnlyMemory<byte>>> batch =
            ((IRazerSetFeatureCommandSession)actor).SubmitBatchAsync(
                [
                    new(0x03, 0x0B, [0xFF, 0x00], 2),
                    new(0x03, 0x0A, [0x05, 0x00], 2),
                ],
                () => TransactionIdAssignment.Valid(
                    checked((byte)Interlocked.Increment(ref batchAssignments))),
                Timeout,
                caller.Token);
        await io.WaitForCallsAsync(1);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.SubmitAsync(
            new(0x03, 0x0B, [0xFF, 0x01], 2),
            () =>
            {
                queuedAssignments++;
                return TransactionIdAssignment.Valid(3);
            },
            Timeout,
            default);

        caller.Cancel();
        firstCompletion.SetResult(SetSuccessResult());

        Assert.Equal(HidSessionFailureKind.Canceled, (await batch).Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, (await queued).Failure);
        Assert.Equal(1, batchAssignments);
        Assert.Equal(0, queuedAssignments);
        Assert.Single(io.Calls);
        await actor.DisposeAsync();
        Assert.Equal(
            RazerCommandSessionDisposition.FullyDisposed,
            actor.Disposition);
    }

    [Fact]
    public async Task SubmitBatchAsync_FailureStopsBatchAndPoisonsQueuedWork()
    {
        var secondCompletion =
            new TaskCompletionSource<HidFeatureIoResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(
            SetSuccess(),
            (_, _, _, _, _) => secondCompletion.Task,
            SetSuccess());
        await using WindowsRazerHidSession actor = CreateActor(io);
        int transaction = 0;

        Task<HidSessionResult<ReadOnlyMemory<byte>>> batch =
            ((IRazerSetFeatureCommandSession)actor).SubmitBatchAsync(
                [
                    new(0x03, 0x0B, [0xFF, 0x00], 2),
                    new(0x03, 0x0B, [0xFF, 0x01], 2),
                    new(0x03, 0x0A, [0x05, 0x00], 2),
                ],
                () => TransactionIdAssignment.Valid(
                    checked((byte)Interlocked.Increment(ref transaction))),
                Timeout,
                default);
        await io.WaitForCallsAsync(2);
        int queuedAssignments = 0;
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.SubmitAsync(
            new(0x03, 0x0B, [0xFF, 0x02], 2),
            () =>
            {
                queuedAssignments++;
                return TransactionIdAssignment.Valid(4);
            },
            Timeout,
            default);
        secondCompletion.SetResult(
            new(
                new NativeIoCompletion(false, 0, 5),
                true,
                HidFeatureTermination.Completed));

        Assert.Equal(
            HidSessionFailureKind.NativeIoFailure,
            (await batch).Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, (await queued).Failure);
        Assert.Equal(0, queuedAssignments);
        Assert.Equal(2, io.Calls.Count);
        await actor.DisposeAsync();
        Assert.Equal(
            RazerCommandSessionDisposition.FullyDisposed,
            actor.Disposition);
    }

    [Fact]
    public async Task SubmitBatchAsync_AdmissionAllocatesLessThanSevenSingles()
    {
        _ = await MeasureSetAdmissionAllocationsAsync(batch: false);
        _ = await MeasureSetAdmissionAllocationsAsync(batch: true);

        long individualBytes =
            await MeasureSetAdmissionAllocationsAsync(batch: false);
        long batchBytes =
            await MeasureSetAdmissionAllocationsAsync(batch: true);

        Assert.True(
            individualBytes - batchBytes >= 1024,
            $"Individual={individualBytes:N0}; batch={batchBytes:N0}");
    }

    [Fact]
    public async Task QueuedCancellationRace_QueuedWinnerConsumesNoId()
    {
        var gate = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo((_, _, _, _, _) => gate.Task, GetSuccess(1, 1, 1, []));
        await using WindowsRazerHidSession actor = CreateActor(io);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> active = actor.ExecuteAsync(
            new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default);
        using var canceled = new CancellationTokenSource();
        int assignments = 0;
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.ExecuteAsync(
            new(1, 2, [], 0), () => { assignments++; return TransactionIdAssignment.Valid(2); }, Timeout, canceled.Token);

        await io.WaitForCallsAsync(1);
        canceled.Cancel();
        Assert.Equal(HidSessionFailureKind.QueuedCanceled, (await queued).Failure);
        Assert.Equal(0, assignments);
        gate.SetResult(SetSuccessResult());
        Assert.Null((await active).Failure);
    }

    [Fact]
    public async Task CancellationAfterClaimConsumesIdButSubmitsNoIoAndStaysHealthy()
    {
        using var caller = new CancellationTokenSource();
        int assignments = 0;
        var io = new ScriptedIo(SetSuccess(), GetSuccess(2, 1, 2, []));
        await using WindowsRazerHidSession actor = CreateActor(io);

        Assert.Equal(
            HidSessionFailureKind.Canceled,
            (await actor.ExecuteAsync(
                new(1, 1, [], 0),
                () => { assignments++; caller.Cancel(); return TransactionIdAssignment.Valid(1); },
                Timeout,
                caller.Token)).Failure);
        Assert.Equal(1, assignments);
        Assert.Empty(io.Calls);
        Assert.Null((await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
    }

    [Fact]
    public async Task QueuedCancellationRace_ClaimedWinnerAssignsExactlyOnceAndMapsCancellation()
    {
        using var caller = new CancellationTokenSource();
        using var releaseAssignment = new ManualResetEventSlim();
        var assignmentEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(SetSuccess(), GetSuccess(2, 1, 2, []));
        await using WindowsRazerHidSession actor = CreateActor(io);
        int assignments = 0;

        Task<HidSessionResult<ReadOnlyMemory<byte>>> claimed = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () =>
            {
                Interlocked.Increment(ref assignments);
                assignmentEntered.TrySetResult(true);
                releaseAssignment.Wait();
                return TransactionIdAssignment.Valid(1);
            },
            Timeout,
            caller.Token);

        await assignmentEntered.Task;
        caller.Cancel();
        releaseAssignment.Set();

        Assert.Equal(HidSessionFailureKind.Canceled, (await claimed).Failure);
        Assert.Equal(1, assignments);
        Assert.Empty(io.Calls);
        Assert.Null((await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
    }

    [Fact]
    public async Task AdmissionSnapshotsMutableArguments()
    {
        var gate = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo((_, _, _, _, _) => gate.Task, GetSuccess(1, 1, 1, []), SetSuccess(), GetSuccess(2, 1, 2, []));
        await using WindowsRazerHidSession actor = CreateActor(io);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> active = actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default);
        byte[] arguments = [0x12];
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.ExecuteAsync(new(1, 2, arguments, 0), () => TransactionIdAssignment.Valid(2), Timeout, default);
        arguments[0] = 0xFE;

        await io.WaitForCallsAsync(1);
        gate.SetResult(SetSuccessResult());
        await Task.WhenAll(active, queued);
        Assert.Equal(0x12, io.Calls[2].Buffer[9]);
    }

    [Fact]
    public async Task InvalidInputsThrowSynchronouslyWithoutAssigning()
    {
        await using WindowsRazerHidSession actor = CreateActor(new ScriptedIo());
        int assignments = 0;
        Func<TransactionIdAssignment> assign = () => { assignments++; return TransactionIdAssignment.Valid(1); };

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = actor.ExecuteAsync(new(1, 1, new byte[81], 0), assign, Timeout, default); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = actor.ExecuteAsync(new(1, 1, [], 81), assign, Timeout, default); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = actor.ExecuteAsync(new(1, 1, [], 0), assign, TimeSpan.Zero, default); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = actor.ExecuteAsync(new(1, 1, [], 0), assign, System.Threading.Timeout.InfiniteTimeSpan, default); });
        Assert.Throws<ArgumentNullException>(() => { _ = actor.ExecuteAsync(new(1, 1, [], 0), null!, Timeout, default); });
        IRazerSetFeatureCommandSession submitter = actor;
        Assert.Throws<ArgumentNullException>(() => { _ = submitter.SubmitBatchAsync(null!, assign, Timeout, default); });
        Assert.Throws<ArgumentException>(() => { _ = submitter.SubmitBatchAsync([], assign, Timeout, default); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = submitter.SubmitBatchAsync([new(1, 1, new byte[81], 0)], assign, Timeout, default); });
        Assert.Equal(0, assignments);
    }

    [Fact]
    public async Task ExchangeTimeoutAcceptsLargestSchedulableValue()
    {
        using var caller = new CancellationTokenSource();
        await using WindowsRazerHidSession actor = CreateActor(new ScriptedIo());
        int assignments = 0;

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () =>
            {
                assignments++;
                caller.Cancel();
                return TransactionIdAssignment.Valid(1);
            },
            TimeSpan.FromMilliseconds(uint.MaxValue - 1d),
            caller.Token);

        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Equal(1, assignments);
    }

    [Fact]
    public async Task ExchangeTimeoutRejectsFirstUnschedulableValueSynchronouslyWithoutAdmissionOrId()
    {
        await using WindowsRazerHidSession actor = CreateActor(new ScriptedIo());
        int assignments = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = actor.ExecuteAsync(
                new(1, 1, [], 0),
                () => { assignments++; return TransactionIdAssignment.Valid(1); },
                TimeSpan.FromMilliseconds(uint.MaxValue),
                default);
        });

        Assert.Equal(0, assignments);
        Assert.Equal(
            HidSessionFailureKind.Exhausted,
            (await actor.ExecuteAsync(new(1, 2, [], 0), TransactionIdAssignment.Exhausted, Timeout, default)).Failure);
    }

    [Fact]
    public async Task ExhaustedIsHealthyButInvalidAndThrowingAssignmentsPoison()
    {
        await using (WindowsRazerHidSession actor = CreateActor(new ScriptedIo()))
        {
            Assert.Equal(HidSessionFailureKind.Exhausted, (await actor.ExecuteAsync(new(1, 1, [], 0), TransactionIdAssignment.Exhausted, Timeout, default)).Failure);
            Assert.Equal(HidSessionFailureKind.Exhausted, (await actor.ExecuteAsync(new(1, 1, [], 0), TransactionIdAssignment.Exhausted, Timeout, default)).Failure);
        }

        foreach (Func<TransactionIdAssignment> invalid in new Func<TransactionIdAssignment>[]
                 { () => default, () => new(0, HidSessionFailureKind.ProtocolFailure), () => new(1, HidSessionFailureKind.Exhausted), () => throw new InvalidOperationException() })
        {
            var io = new ScriptedIo();
            WindowsRazerHidSession actor = CreateActor(io);
            Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await actor.ExecuteAsync(new(1, 1, [], 0), invalid, Timeout, default)).Failure);
            Assert.Contains(
                (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure,
                new HidSessionFailureKind?[] { HidSessionFailureKind.Poisoned, HidSessionFailureKind.Disposed });
            Assert.Empty(io.Calls);
            await actor.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(GuardKind.Conflict, HidSessionFailureKind.ConflictBlocked)]
    [InlineData(GuardKind.DetectionFailure, HidSessionFailureKind.ConflictDetectionFailed)]
    [InlineData(GuardKind.Throw, HidSessionFailureKind.ConflictDetectionFailed)]
    public async Task GuardFailuresUseZeroIoAndKeepActorHealthy(GuardKind kind, HidSessionFailureKind expected)
    {
        var guard = new ScriptedGuard(kind, GuardKind.Success);
        var io = new ScriptedIo(SetSuccess(), GetSuccess(2, 1, 2, []));
        await using WindowsRazerHidSession actor = CreateActor(io, guard: guard);

        HidSessionResult<ReadOnlyMemory<byte>> blocked = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);
        Assert.Equal(expected, blocked.Failure);
        Assert.Equal(
            new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.Guard,
                expected,
                NativeWin32Error: null,
                TransferredByteCount: null),
            blocked.Diagnostic);
        Assert.Null((await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
        Assert.Equal(2, io.Calls.Count);
    }

    [Fact]
    public async Task ExchangeUsesExactCodesFreshBuffersAndAcceptsUnnumberedNinetyByteGetCompletion()
    {
        var io = new ScriptedIo(SetSuccess(bytes: 1), GetSuccess(1, 0x0D, 2, [0x55]));
        await using WindowsRazerHidSession actor = CreateActor(io);
        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(new(0x0D, 2, [0x44], 1), () => TransactionIdAssignment.Valid(1), Timeout, default);

        Assert.Null(result.Failure);
        Assert.Equal([SetCode, GetCode], io.Calls.Select(c => c.ControlCode));
        Assert.All(io.Calls, c => Assert.Equal(91, c.Buffer.Length));
        Assert.Equal(0, io.Calls[0].Buffer[0]);
        Assert.Equal(0, io.Calls[1].Buffer[0]);
        Assert.NotSame(io.Calls[0].Buffer, io.Calls[1].Buffer);

        var shortIo = new ScriptedIo(SetSuccess(), GetSuccess(2, 1, 1, [], bytes: 90));
        await using WindowsRazerHidSession shortActor = CreateActor(shortIo);
        HidSessionResult<ReadOnlyMemory<byte>> shortResult = await shortActor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(2),
            Timeout,
            default);
        Assert.Null(shortResult.Failure);
        Assert.Null(shortResult.Diagnostic);
    }

    [Theory]
    [InlineData(89, 0, HidSessionFailureKind.NativeIoFailure)]
    [InlineData(90, 1, HidSessionFailureKind.NativeIoFailure)]
    [InlineData(91, 1, HidSessionFailureKind.ProtocolFailure)]
    public async Task GetCompletionRejectsOtherShortLengthsAndNonzeroReportIds(
        int bytesTransferred,
        byte reportId,
        HidSessionFailureKind expected)
    {
        Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> get =
            (_, _, buffer, _, _) =>
            {
                Response(1, 1, 1, []).CopyTo(buffer, 0);
                buffer[0] = reportId;
                return Task.FromResult(new HidFeatureIoResult(
                    new NativeIoCompletion(true, bytesTransferred, 0),
                    WasSubmitted: true,
                    Termination: HidFeatureTermination.Completed));
            };
        await using WindowsRazerHidSession actor = CreateActor(
            new ScriptedIo(SetSuccess(), get));

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Equal(expected, result.Failure);
    }

    [Theory]
    [InlineData(false, HidSessionFailurePhase.SetSubmit)]
    [InlineData(true, HidSessionFailurePhase.GetSubmit)]
    public async Task ImmediateNativeFailureReportsSubmitPhase(bool getPhase, HidSessionFailurePhase phase)
    {
        Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> failure =
            Result(new HidFeatureIoResult(
                new NativeIoCompletion(false, 0, 31),
                WasSubmitted: false,
                Termination: HidFeatureTermination.Completed));
        var io = getPhase
            ? new ScriptedIo(SetSuccess(), failure)
            : new ScriptedIo(failure);
        WindowsRazerHidSession actor = CreateActor(io);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Equal(
            new HidSessionFailureDiagnostic(
                phase,
                HidSessionFailureKind.NativeIoFailure,
                NativeWin32Error: 31,
                TransferredByteCount: 0),
            result.Diagnostic);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task SetCompletionThenCancellationBeforeGetPoisonsAndSubmitsNoGet()
    {
        using var caller = new CancellationTokenSource();
        var io = new ScriptedIo((_, _, _, _, _) => { caller.Cancel(); return Task.FromResult(SetSuccessResult()); });
        WindowsRazerHidSession actor = CreateActor(io);

        Assert.Equal(HidSessionFailureKind.Canceled, (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, caller.Token)).Failure);
        Assert.Single(io.Calls);
        Assert.Contains(
            (await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure,
            new HidSessionFailureKind?[] { HidSessionFailureKind.Poisoned, HidSessionFailureKind.Disposed });
        await actor.DisposeAsync();
    }

    [Theory]
    [InlineData(1, false, HidSessionFailureKind.Canceled, false)]
    [InlineData(2, false, HidSessionFailureKind.Timeout, false)]
    [InlineData(1, true, HidSessionFailureKind.Canceled, true)]
    [InlineData(2, true, HidSessionFailureKind.Timeout, true)]
    [InlineData(0, true, HidSessionFailureKind.NativeIoFailure, true)]
    public async Task SetResultMappingMatchesTaskSeven(int terminationValue, bool submitted, HidSessionFailureKind failure, bool poisoned)
    {
        var termination = (HidFeatureTermination)terminationValue;
        var io = new ScriptedIo(Result(new HidFeatureIoResult(null, submitted, termination)));
        WindowsRazerHidSession actor = CreateActor(io);
        Assert.Equal(failure, (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default)).Failure);
        HidSessionFailureKind? next = (await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure;
        if (poisoned)
        {
            Assert.Contains(next, new HidSessionFailureKind?[] { HidSessionFailureKind.Poisoned, HidSessionFailureKind.Disposed });
        }
        else
        {
            Assert.Equal(HidSessionFailureKind.NativeIoFailure, next);
        }
        await actor.DisposeAsync();
    }

    [Theory]
    [InlineData(1, HidSessionFailureKind.Canceled)]
    [InlineData(2, HidSessionFailureKind.Timeout)]
    [InlineData(0, HidSessionFailureKind.NativeIoFailure)]
    public async Task GetResultFailuresAlwaysPoison(int terminationValue, HidSessionFailureKind expected)
    {
        HidFeatureTermination termination = (HidFeatureTermination)terminationValue;
        NativeIoCompletion? completion = termination == HidFeatureTermination.Completed
            ? new NativeIoCompletion(false, 0, 5)
            : null;
        var io = new ScriptedIo(
            SetSuccess(),
            Result(new HidFeatureIoResult(completion, true, termination)));
        WindowsRazerHidSession actor = CreateActor(io);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(
            new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.GetCompletion,
                expected,
                completion?.ErrorCode,
                completion?.BytesTransferred),
            result.Diagnostic);
        Assert.Contains(
            (await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure,
            new HidSessionFailureKind?[] { HidSessionFailureKind.Poisoned, HidSessionFailureKind.Disposed });
        await actor.DisposeAsync();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CompletedInvalidNativeCompletionBeatsConcurrentCancellation(
        bool getPhase,
        bool failedCompletion,
        bool deadlineCancellation)
    {
        using var caller = new CancellationTokenSource();
        var time = new ManualTimeProvider();
        NativeIoCompletion? completion = failedCompletion
            ? new NativeIoCompletion(false, 0, 5)
            : null;
        Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> invalid =
            (_, _, _, _, _) =>
            {
                if (deadlineCancellation)
                {
                    time.Advance(Timeout);
                }
                else
                {
                    caller.Cancel();
                }

                return Task.FromResult(new HidFeatureIoResult(
                    completion,
                    true,
                    HidFeatureTermination.Completed));
            };
        var io = getPhase
            ? new ScriptedIo(SetSuccess(), invalid)
            : new ScriptedIo(invalid);
        WindowsRazerHidSession actor = CreateActor(io, time: time);

        Assert.Equal(
            HidSessionFailureKind.NativeIoFailure,
            (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, caller.Token)).Failure);
        Assert.Contains(
            (await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure,
            new HidSessionFailureKind?[] { HidSessionFailureKind.Poisoned, HidSessionFailureKind.Disposed });
        await actor.DisposeAsync();
    }

    [Theory]
    [InlineData(true, HidSessionFailurePhase.SetSubmit)]
    [InlineData(false, HidSessionFailurePhase.GetSubmit)]
    public async Task TaskSevenFaultMapsNativeFailureAndPoisons(
        bool faultSet,
        HidSessionFailurePhase phase)
    {
        Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> fault =
            (_, _, _, _, _) => Task.FromException<HidFeatureIoResult>(new IOException("native"));
        var io = faultSet
            ? new ScriptedIo(fault)
            : new ScriptedIo(SetSuccess(), fault);
        WindowsRazerHidSession actor = CreateActor(io);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);
        Assert.Equal(HidSessionFailureKind.NativeIoFailure, result.Failure);
        Assert.Equal(
            new HidSessionFailureDiagnostic(
                phase,
                HidSessionFailureKind.NativeIoFailure,
                NativeWin32Error: null,
                TransferredByteCount: null),
            result.Diagnostic);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task CallerWinsSimultaneousCancellationBeforeSetAndActorStaysHealthy()
    {
        using var caller = new CancellationTokenSource();
        var time = new ManualTimeProvider();
        int checks = 0;
        var guard = new CallbackGuard(() =>
        {
            if (Interlocked.Increment(ref checks) == 1)
            {
                time.Advance(Timeout);
                caller.Cancel();
            }
        });
        var io = new ScriptedIo(SetSuccess(), GetSuccess(2, 1, 2, []));
        await using WindowsRazerHidSession actor = CreateActor(io, guard, time);

        Assert.Equal(HidSessionFailureKind.Canceled, (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, caller.Token)).Failure);
        Assert.Empty(io.Calls);
        Assert.Null((await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
    }

    [Fact]
    public async Task QueueTimeDoesNotConsumeDeadlineButGuardTimeDoes()
    {
        var activeGate = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ManualTimeProvider();
        var guard = new ScriptedGuard(GuardKind.Success, GuardKind.Success);
        var io = new ScriptedIo((_, _, _, _, _) => activeGate.Task, GetSuccess(1, 1, 1, []), SetSuccess(), GetSuccess(2, 1, 2, []));
        await using WindowsRazerHidSession actor = CreateActor(io, guard, time);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> first = actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), TimeSpan.FromHours(2), default);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> second = actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default);

        await io.WaitForCallsAsync(1);
        time.Advance(TimeSpan.FromHours(1));
        activeGate.SetResult(SetSuccessResult());
        Assert.Null((await first).Failure);
        Assert.Null((await second).Failure);
    }

    [Fact]
    public async Task ModeSubmissionAdmission_IsRecheckedAfterQueuedWorkFinishes()
    {
        bool active = true;
        var release = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo((_, _, _, _, _) => release.Task, GetSuccess(1, 1, 1, []));
        await using WindowsRazerHidSession actor = CreateActor(io);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> first = actor.ExecuteAsync(
            new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default);
        await io.WaitForCallsAsync(1);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> driver = actor.ExecuteAsync(
            new RazerCommandSpec(0, 4, [3, 0], 2) { SubmissionAllowed = () => active },
            () => TransactionIdAssignment.Valid(2), Timeout, default);

        active = false;
        release.TrySetResult(SetSuccessResult());
        Assert.Null((await first).Failure);
        HidSessionResult<ReadOnlyMemory<byte>> result = await driver;

        Assert.Equal(HidSessionFailureKind.ConflictBlocked, result.Failure);
        Assert.Equal(HidSessionFailurePhase.Guard, result.Diagnostic?.Phase);
        Assert.Equal(2, io.Calls.Count);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
    }

    [Fact]
    public async Task ModeSubmissionAdmission_IsRecheckedAfterNativeSafetyGuard()
    {
        bool active = true;
        var io = new ScriptedIo();
        await using WindowsRazerHidSession actor = CreateActor(io, new CallbackGuard(() => active = false));

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new RazerCommandSpec(0, 4, [3, 0], 2) { SubmissionAllowed = () => active },
            () => TransactionIdAssignment.Valid(1), Timeout, default);

        Assert.Equal(HidSessionFailureKind.ConflictBlocked, result.Failure);
        Assert.Empty(io.Calls);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
    }

    [Fact]
    public async Task SetAndGetReuseOneDeadlineTokenWithOnlyRemainingTime()
    {
        var time = new ManualTimeProvider();
        CancellationToken setDeadline = default;
        CancellationToken getDeadline = default;
        var io = new ScriptedIo(
            (_, _, _, _, deadline) =>
            {
                setDeadline = deadline;
                time.Advance(TimeSpan.FromSeconds(4));
                return Task.FromResult(SetSuccessResult());
            },
            (_, _, buffer, _, deadline) =>
            {
                getDeadline = deadline;
                Assert.False(deadline.IsCancellationRequested);
                time.Advance(TimeSpan.FromSeconds(6));
                Assert.True(deadline.IsCancellationRequested);
                Response(1, 1, 1, []).CopyTo(buffer, 0);
                return Task.FromResult(new HidFeatureIoResult(
                    new NativeIoCompletion(true, 91, 0),
                    true,
                    HidFeatureTermination.Completed));
            });
        WindowsRazerHidSession actor = CreateActor(io, time: time);

        Assert.Equal(
            HidSessionFailureKind.Timeout,
            (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default)).Failure);
        Assert.Equal(setDeadline, getDeadline);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task PoisonAdmissionRace_PoisonWinnerRejectsWhileDrainingThenBecomesDisposed()
    {
        byte[] malformed = Response(1, 1, 1, []);
        malformed[88] ^= 1;
        var enteredDisposal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDisposal = new ManualResetEventSlim();
        var io = new ScriptedIo(SetSuccess(), GetResult(malformed));
        int rejectedAssignments = 0;
        WindowsRazerHidSession actor = CreateActor(
            io,
            disposeBound: () =>
            {
                enteredDisposal.TrySetResult(true);
                releaseDisposal.Wait();
            });

        HidSessionResult<ReadOnlyMemory<byte>> malformedResult = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, malformedResult.Failure);
        Assert.Equal(
            new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.ProtocolValidation,
                HidSessionFailureKind.ProtocolFailure,
                NativeWin32Error: 0,
                TransferredByteCount: 91)
            {
                ProtocolValidationFailure = HidProtocolValidationFailure.Checksum,
            },
            malformedResult.Diagnostic);
        await enteredDisposal.Task;
        Assert.Equal(
            HidSessionFailureKind.Poisoned,
            (await actor.ExecuteAsync(
                new(1, 2, [], 0),
                () => { rejectedAssignments++; return TransactionIdAssignment.Valid(2); },
                Timeout,
                default)).Failure);
        Assert.Equal(0, rejectedAssignments);
        Assert.Equal(2, io.Calls.Count);
        releaseDisposal.Set();
        await actor.DisposeAsync();
        Assert.Equal(
            HidSessionFailureKind.Disposed,
            (await actor.ExecuteAsync(new(1, 3, [], 0), () => TransactionIdAssignment.Valid(3), Timeout, default)).Failure);
    }

    [Fact]
    public async Task PoisonAdmissionRace_AdmissionWinnerIsDrainedBeforeItCanPerformIo()
    {
        byte[] malformed = Response(1, 1, 1, []);
        malformed[88] ^= 1;
        var getEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGet = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(
            SetSuccess(),
            (_, _, buffer, _, _) =>
            {
                malformed.CopyTo(buffer, 0);
                getEntered.TrySetResult(true);
                return releaseGet.Task;
            },
            SetSuccess());
        WindowsRazerHidSession actor = CreateActor(io);
        int queuedAssignments = 0;
        Task<HidSessionResult<ReadOnlyMemory<byte>>> first = actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default);
        await getEntered.Task;
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.ExecuteAsync(
            new(1, 2, [], 0),
            () => { queuedAssignments++; return TransactionIdAssignment.Valid(2); },
            Timeout,
            default);
        releaseGet.SetResult(new HidFeatureIoResult(
            new NativeIoCompletion(true, 91, 0),
            true,
            HidFeatureTermination.Completed));

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await first).Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, (await queued).Failure);
        Assert.Equal(0, queuedAssignments);
        Assert.Equal(2, io.Calls.Count);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task FeatureReportSettleDelaysGetUntilConfiguredDurationElapsed()
    {
        var time = new ManualTimeProvider();
        var io = new ScriptedIo(SetSuccess(), GetSuccess(1, 1, 1, []));
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            time: time,
            featureReportSettleDelay: TimeSpan.FromMilliseconds(10));

        Task<HidSessionResult<ReadOnlyMemory<byte>>> command = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);
        await io.WaitForCallsAsync(1);
        using var timerReadiness =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await time.WaitForTimerCountAsync(2, timerReadiness.Token);

        time.Advance(TimeSpan.FromMilliseconds(9));
        await Task.Yield();
        Assert.Single(io.Calls);
        Assert.False(command.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null((await command).Failure);
        Assert.Equal([SetCode, GetCode], io.Calls.Select(call => call.ControlCode));
    }

    [Fact]
    public async Task ZeroFeatureReportSettleSubmitsGetImmediately()
    {
        var time = new ManualTimeProvider();
        var io = new ScriptedIo(SetSuccess(), GetSuccess(1, 1, 1, []));
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            time: time,
            featureReportSettleDelay: TimeSpan.Zero);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Null(result.Failure);
        Assert.Equal([SetCode, GetCode], io.Calls.Select(call => call.ControlCode));
        Assert.Equal(DateTimeOffset.UnixEpoch, time.GetUtcNow());
    }

    [Fact]
    public async Task CallerCancellationDuringFeatureReportSettlePoisonsAndSubmitsNoGet()
    {
        using var caller = new CancellationTokenSource();
        using var releaseDisposal = new ManualResetEventSlim();
        var time = new ManualTimeProvider();
        var io = new ScriptedIo(SetSuccess());
        WindowsRazerHidSession actor = CreateActor(
            io,
            time: time,
            disposeBound: releaseDisposal.Wait,
            featureReportSettleDelay: TimeSpan.FromMilliseconds(10));

        Task<HidSessionResult<ReadOnlyMemory<byte>>> command = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            caller.Token);
        await io.WaitForCallsAsync(1);
        caller.Cancel();

        try
        {
            Assert.Equal(HidSessionFailureKind.Canceled, (await command).Failure);
            Assert.Single(io.Calls);
            Assert.Equal(RazerCommandSessionDisposition.RecoveryRequired, actor.Disposition);
            Assert.Equal(
                HidSessionFailureKind.Poisoned,
                (await actor.ExecuteAsync(
                    new(1, 2, [], 0),
                    () => TransactionIdAssignment.Valid(2),
                    Timeout,
                    default)).Failure);
        }
        finally
        {
            releaseDisposal.Set();
            await actor.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeadlineExpiryDuringFeatureReportSettlePoisonsAndSubmitsNoGet()
    {
        using var releaseDisposal = new ManualResetEventSlim();
        var time = new ManualTimeProvider();
        var io = new ScriptedIo(SetSuccess());
        WindowsRazerHidSession actor = CreateActor(
            io,
            time: time,
            disposeBound: releaseDisposal.Wait,
            featureReportSettleDelay: TimeSpan.FromMilliseconds(20));

        Task<HidSessionResult<ReadOnlyMemory<byte>>> command = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            TimeSpan.FromMilliseconds(10),
            default);
        await io.WaitForCallsAsync(1);
        time.Advance(TimeSpan.FromMilliseconds(10));

        try
        {
            Assert.Equal(HidSessionFailureKind.Timeout, (await command).Failure);
            Assert.Single(io.Calls);
            Assert.Equal(RazerCommandSessionDisposition.RecoveryRequired, actor.Disposition);
            Assert.Equal(
                HidSessionFailureKind.Poisoned,
                (await actor.ExecuteAsync(
                    new(1, 2, [], 0),
                    () => TransactionIdAssignment.Valid(2),
                    Timeout,
                    default)).Failure);
        }
        finally
        {
            releaseDisposal.Set();
            await actor.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownBatteryQueryValue_PoisonsBeforeQueuedSecondIo()
    {
        var getEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGet = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(
            SetSuccess(),
            (_, _, buffer, _, _) =>
            {
                Response(1, 0x07, 0x92, [0xFF]).CopyTo(buffer, 0);
                getEntered.TrySetResult(true);
                return releaseGet.Task;
            },
            SetSuccess());
        await using WindowsRazerHidSession actor = CreateActor(io);
        var client = new BatteryCommandClient(actor, Timeout);

        Task<HidSessionResult<BatteryValidationSetting>> query = client.QueryAsync(1, default);
        await getEntered.Task;
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(2),
            Timeout,
            default);
        releaseGet.SetResult(new HidFeatureIoResult(
            new NativeIoCompletion(true, 91, 0),
            true,
            HidFeatureTermination.Completed));

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await query).Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, (await queued).Failure);
        Assert.Equal(2, io.Calls.Count);
    }

    [Theory]
    [InlineData(GuardKind.Conflict)]
    [InlineData(GuardKind.DetectionFailure)]
    [InlineData(GuardKind.Throw)]
    public async Task AdvisorySkipsPerOperationControllerPresenceGuard(GuardKind kind)
    {
        var io = new ScriptedIo(SetSuccess(), GetSuccess(1, 1, 1, []));
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            new ScriptedGuard(kind),
            policy: ControllerExclusivityPolicy.Advisory);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Null(result.Failure);
        Assert.Equal(2, io.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvisoryStillRejectsMalformedAndMismatchedResponses(bool transactionMismatch)
    {
        byte[] response = Response(1, 1, 1, []);
        if (transactionMismatch)
        {
            response[2] = 2;
            Rechecksum(response);
        }
        else
        {
            response[89] ^= 1;
        }

        var io = new ScriptedIo(SetSuccess(), GetResult(response));
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            policy: ControllerExclusivityPolicy.Advisory);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
    }

    [Fact]
    public async Task IncorrectBatterySetterAck_PoisonsBeforeQueuedSecondIo()
    {
        var getEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGet = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo(
            SetSuccess(),
            (_, _, buffer, _, _) =>
            {
                Response(1, 0x07, 0x12, [0xC1]).CopyTo(buffer, 0);
                getEntered.TrySetResult(true);
                return releaseGet.Task;
            },
            SetSuccess());
        await using WindowsRazerHidSession actor = CreateActor(io);
        var client = new BatteryCommandClient(actor, Timeout);

        Task<HidSessionResult<HidUnit>> set =
            client.SetAsync(1, BatteryValidationSetting.Limit70, default);
        await getEntered.Task;
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.ExecuteAsync(
            new(1, 1, [], 0),
            () => TransactionIdAssignment.Valid(2),
            Timeout,
            default);
        releaseGet.SetResult(new HidFeatureIoResult(
            new NativeIoCompletion(true, 91, 0),
            true,
            HidFeatureTermination.Completed));

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await set).Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, (await queued).Failure);
        Assert.Equal(2, io.Calls.Count);
    }

    [Fact]
    public async Task UnknownBatteryCandidateQuery_ForcesRecoveryActorForRestoration()
    {
        var primaryIo = new ScriptedIo(
            SetSuccess(), GetSuccess(1, 0x07, 0x92, [0xD0]),
            SetSuccess(), GetSuccess(2, 0x07, 0x12, [0xC6]),
            SetSuccess(), GetSuccess(3, 0x07, 0x92, [0xFF]));
        var recoveryIo = new ScriptedIo(
            SetSuccess(), GetSuccess(4, 0x07, 0x12, [0xD0]),
            SetSuccess(), GetSuccess(5, 0x07, 0x92, [0xD0]));
        IBatteryValidationPort port = await OpenValidationPortAsync(primaryIo, recoveryIo);

        Assert.Equal(BatteryValidationSetting.Limit80, (await port.QueryAsync(default)).Value);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await port.QueryAsync(default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80, (await port.QueryAsync(default)).Value);

        Assert.Equal(6, primaryIo.Calls.Count);
        Assert.Equal(4, recoveryIo.Calls.Count);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task IncorrectBatteryCandidateSetterAck_ForcesRecoveryActorForRestoration()
    {
        var primaryIo = new ScriptedIo(
            SetSuccess(), GetSuccess(1, 0x07, 0x92, [0xD0]),
            SetSuccess(), GetSuccess(2, 0x07, 0x12, [0xC1]));
        var recoveryIo = new ScriptedIo(
            SetSuccess(), GetSuccess(4, 0x07, 0x12, [0xD0]),
            SetSuccess(), GetSuccess(5, 0x07, 0x92, [0xD0]));
        IBatteryValidationPort port = await OpenValidationPortAsync(primaryIo, recoveryIo);

        Assert.Equal(BatteryValidationSetting.Limit80, (await port.QueryAsync(default)).Value);
        Assert.Equal(
            HidSessionFailureKind.ProtocolFailure,
            (await port.ApplyAsync(BatteryValidationSetting.Limit70, default)).Failure);
        Assert.Null((await port.ApplyAsync(BatteryValidationSetting.Limit80, default)).Failure);
        Assert.Equal(BatteryValidationSetting.Limit80, (await port.QueryAsync(default)).Value);

        Assert.Equal(4, primaryIo.Calls.Count);
        Assert.Equal(4, recoveryIo.Calls.Count);
        await port.DisposeAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ResponseValidationRejectsReportTransactionCommandStatusChecksumAndPayloadBounds(int corruption)
    {
        byte[] response = Response(1, 1, 1, []);
        switch (corruption)
        {
            case 0: response[0] = 1; break;
            case 1: response[2] = 2; Rechecksum(response); break;
            case 2: response[7] = 2; Rechecksum(response); break;
            case 3: response[8] = 2; Rechecksum(response); break;
            case 4: response[1] = (byte)RazerCommandStatus.NotSupported; Rechecksum(response); break;
            case 5: response[89] ^= 1; break;
            case 6: response[6] = 81; Rechecksum(response); break;
        }

        var io = new ScriptedIo(SetSuccess(), GetResult(response));
        WindowsRazerHidSession actor = CreateActor(io);
        Assert.Equal(
            HidSessionFailureKind.ProtocolFailure,
            (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default)).Failure);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWaitsForActiveThenDisposesHandleBeforeLeaseAndSharesFailure()
    {
        var gate = new TaskCompletionSource<HidFeatureIoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var context = new ValidationRunContext();
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease lease));
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        var handle = new OpenedHidHandle(
            file,
            bound,
            () => { events.Add("bound"); throw new IOException("close"); },
            () =>
            {
                events.Add("file");
                Assert.False(context.TryAcquireActor(out _));
            });
        var io = new ScriptedIo((_, _, _, _, _) => gate.Task);
        WindowsRazerHidSession actor = new(
            handle,
            io,
            new ScriptedGuard(),
            TimeProvider.System,
            lease,
            ControllerExclusivityPolicy.Strict);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> active = actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default);
        await io.WaitForCallsAsync(1);

        Task firstDispose = actor.DisposeAsync().AsTask();
        Task secondDispose = actor.DisposeAsync().AsTask();
        Assert.False(firstDispose.IsCompleted);
        Assert.Empty(events);
        Assert.Equal(HidSessionFailureKind.Disposed, (await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
        gate.SetResult(new HidFeatureIoResult(null, false, HidFeatureTermination.CallerCanceled));
        IOException first = await Assert.ThrowsAsync<IOException>(() => firstDispose);
        IOException second = await Assert.ThrowsAsync<IOException>(() => secondDispose);
        Assert.Same(first, second);
        Assert.Equal(["bound", "file"], events);
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease replacement));
        replacement.Dispose();
        Assert.Equal(HidSessionFailureKind.Canceled, (await active).Failure);
    }

    [Fact]
    public async Task ConstructorValidationLeavesBorrowedResourcesOwnedByCaller()
    {
        var events = new List<string>();
        (OpenedHidHandle handle, ValidationRunContext.ActorLease lease) = Resources(events);
        Assert.Throws<ArgumentNullException>(() => new WindowsRazerHidSession(
            handle,
            null!,
            new ScriptedGuard(),
            TimeProvider.System,
            lease,
            ControllerExclusivityPolicy.Strict));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsRazerHidSession(
            handle,
            new ScriptedIo(),
            new ScriptedGuard(),
            TimeProvider.System,
            lease,
            ControllerExclusivityPolicy.Strict,
            System.Threading.Timeout.InfiniteTimeSpan));
        Assert.Empty(events);

        await handle.DisposeAsync();
        lease.Dispose();
        Assert.Equal(["bound", "file"], events);
    }

    private static async Task<long> MeasureSetAdmissionAllocationsAsync(
        bool batch)
    {
        var activeCompletion =
            new TaskCompletionSource<HidFeatureIoResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var script =
            new Func<OpenedHidHandle, uint, byte[], CancellationToken,
                CancellationToken, Task<HidFeatureIoResult>>[8];
        script[0] = (_, _, _, _, _) => activeCompletion.Task;
        for (int index = 1; index < script.Length; index++)
        {
            script[index] = SetSuccess();
        }

        var io = new ScriptedIo(script);
        WindowsRazerHidSession actor = CreateActor(io);
        try
        {
            IRazerSetFeatureCommandSession submitter = actor;
            Task<HidSessionResult<ReadOnlyMemory<byte>>> active =
                submitter.SubmitAsync(
                    new(0x03, 0x0B, [0xFF, 0x00], 2),
                    static () => TransactionIdAssignment.Valid(1),
                    Timeout,
                    default);
            await io.WaitForCallsAsync(1);

            var commands = new RazerCommandSpec[7];
            for (int row = 0; row < 6; row++)
            {
                byte[] arguments =
                    new byte[Rz090528MatrixRowProtocol.RowPayloadLength];
                arguments[0] = 0xFF;
                arguments[1] = checked((byte)row);
                commands[row] = new(0x03, 0x0B, arguments, arguments.Length);
            }
            commands[^1] = new(0x03, 0x0A, [0x05, 0x00], 2);
            var queued =
                new Task<HidSessionResult<ReadOnlyMemory<byte>>>[commands.Length];
            Func<TransactionIdAssignment> assign =
                static () => TransactionIdAssignment.Valid(2);

            long before = GC.GetAllocatedBytesForCurrentThread();
            if (batch)
            {
                queued[0] = submitter.SubmitBatchAsync(
                    commands,
                    assign,
                    Timeout,
                    default);
            }
            else
            {
                for (int index = 0; index < commands.Length; index++)
                {
                    queued[index] = submitter.SubmitAsync(
                        commands[index],
                        assign,
                        Timeout,
                        default);
                }
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() - before;

            activeCompletion.SetResult(SetSuccessResult());
            Assert.Null((await active).Failure);
            int queuedCount = batch ? 1 : queued.Length;
            for (int index = 0; index < queuedCount; index++)
            {
                Assert.Null((await queued[index]).Failure);
            }

            return allocated;
        }
        finally
        {
            activeCompletion.TrySetResult(SetSuccessResult());
            await actor.DisposeAsync();
        }
    }

    private const uint SetCode = 0x000B0191;
    private const uint GetCode = 0x000B0192;

    [Fact]
    public async Task PayloadValidatorRejection_RetainsOnlyBoundedRejectedPayload()
    {
        byte[] payload = Enumerable.Range(0, 20).Select(value => (byte)(value + 0xA0)).ToArray();
        var io = new ScriptedIo(SetSuccess(), GetResult(Response(1, 0x07, 0x8F, payload)));
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            policy: ControllerExclusivityPolicy.Advisory);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(0x07, 0x8F, [0x00], payload.Length, _ => false),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(
            HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
        Assert.Equal(
            "A0 A1 A2 A3 A4 A5 A6 A7 A8 A9 AA AB AC AD AE AF ...",
            result.Diagnostic?.RejectedPayloadHex);
    }

    [Fact]
    public async Task ThrowingPayloadValidator_RetainsNoPayloadAndIsNotIsolated()
    {
        var io = new ScriptedIo(SetSuccess(), GetResult(Response(1, 0x07, 0x8F, [0x05])));
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            policy: ControllerExclusivityPolicy.Advisory);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(0x07, 0x8F, [0x00], 1, _ => throw new InvalidOperationException("validator fault")),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(
            HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
        Assert.Null(result.Diagnostic?.RejectedPayloadHex);
        Assert.False(DeviceStateContentRejection.IsContentRejection(
            new RazerProtocolException(
                "validator fault",
                HidProtocolValidationFailure.PayloadValidator)
            {
                StateQuery = DeviceStateQuery.TemporaryFullCharge,
                RejectedPayloadHex = result.Diagnostic?.RejectedPayloadHex,
            }));
    }

    [Fact]
    public async Task ChannelValidationFailure_RetainsNoPayload()
    {
        var io = new ScriptedIo(SetSuccess(), GetResult(Response(2, 0x07, 0x8F, [0x05])));
        await using WindowsRazerHidSession actor = CreateActor(
            io,
            policy: ControllerExclusivityPolicy.Advisory);

        HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
            new(0x07, 0x8F, [0x00], 1, _ => false),
            () => TransactionIdAssignment.Valid(1),
            Timeout,
            default);

        Assert.Equal(
            HidProtocolValidationFailure.TransactionMismatch,
            result.Diagnostic?.ProtocolValidationFailure);
        Assert.Null(result.Diagnostic?.RejectedPayloadHex);
    }

    private static WindowsRazerHidSession CreateActor(
        ScriptedIo io,
        IHardwareAccessGuard? guard = null,
        TimeProvider? time = null,
        List<string>? events = null,
        Action? disposeBound = null,
        TimeSpan featureReportSettleDelay = default,
        ControllerExclusivityPolicy policy = ControllerExclusivityPolicy.Strict)
    {
        (OpenedHidHandle handle, ValidationRunContext.ActorLease lease) = Resources(events, disposeBound);
        return new WindowsRazerHidSession(
            handle,
            io,
            guard ?? new ScriptedGuard(),
            time ?? TimeProvider.System,
            lease,
            policy,
            featureReportSettleDelay);
    }

    private static async Task<IBatteryValidationPort> OpenValidationPortAsync(params ScriptedIo[] io)
    {
        var scripts = new Queue<ScriptedIo>(io);
        var actors = new ValidationActorFactory(
            () => new HidSessionResult<OpenedHidHandle>(CreateHandle(), null),
            (handle, lease) => new WindowsRazerHidSession(
                handle,
                scripts.Dequeue(),
                new ScriptedGuard(),
                TimeProvider.System,
                lease,
                ControllerExclusivityPolicy.Strict),
            Timeout);
        var factory = new BatteryValidationRunFactory(
            () => new ValidationRunContext(),
            actors,
            TimeProvider.System,
            Timeout);

        HidSessionResult<IBatteryValidationPort> opened = await factory.OpenAsync(default);
        Assert.Null(opened.Failure);
        return Assert.IsAssignableFrom<IBatteryValidationPort>(opened.Value);
    }

    private static OpenedHidHandle CreateHandle()
    {
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        return new OpenedHidHandle(file, bound, () => { }, () => { });
    }

    private static (OpenedHidHandle, ValidationRunContext.ActorLease) Resources(List<string>? events = null, Action? disposeBound = null)
    {
        events ??= [];
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        var handle = new OpenedHidHandle(file, bound, disposeBound ?? (() => events.Add("bound")), () => events.Add("file"));
        var context = new ValidationRunContext();
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease lease));
        Assert.False(context.TryAcquireActor(out _));
        return (handle, lease);
    }

    private static Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> SetSuccess(int bytes = 91) =>
        (_, _, _, _, _) => Task.FromResult(new HidFeatureIoResult(new NativeIoCompletion(true, bytes, 0), true, HidFeatureTermination.Completed));
    private static HidFeatureIoResult SetSuccessResult() => new(new NativeIoCompletion(true, 91, 0), true, HidFeatureTermination.Completed);
    private static Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> Result(HidFeatureIoResult value) => (_, _, _, _, _) => Task.FromResult(value);
    private static Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> GetSuccess(byte tx, byte cls, byte cmd, byte[] payload, int bytes = 91) =>
        (_, _, buffer, _, _) => { Response(tx, cls, cmd, payload).CopyTo(buffer, 0); return Task.FromResult(new HidFeatureIoResult(new NativeIoCompletion(true, bytes, 0), true, HidFeatureTermination.Completed)); };
    private static Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> GetResult(byte[] response) =>
        (_, _, buffer, _, _) => { response.CopyTo(buffer, 0); return Task.FromResult(new HidFeatureIoResult(new NativeIoCompletion(true, 91, 0), true, HidFeatureTermination.Completed)); };
    private static byte[] Response(byte tx, byte cls, byte cmd, byte[] payload)
    {
        byte[] report = RazerReport.Create(tx, cls, cmd, payload).Encode();
        report[0] = (byte)RazerCommandStatus.Successful;
        byte checksum = 0;
        for (int i = 2; i <= 87; i++)
        {
            checksum ^= report[i];
        }

        report[88] = checksum;
        byte[] feature = new byte[91];
        report.CopyTo(feature, 1);
        return feature;
    }

    private static void Rechecksum(byte[] feature)
    {
        byte checksum = 0;
        for (int i = 3; i <= 88; i++)
        {
            checksum ^= feature[i];
        }

        feature[89] = checksum;
    }

    public enum GuardKind { Success, Conflict, DetectionFailure, Throw }

    private sealed class ScriptedGuard(params GuardKind[] script) : IHardwareAccessGuard
    {
        private readonly Queue<GuardKind> values = new(script.Length == 0 ? [GuardKind.Success] : script);
        public HardwareAccessDecision Check()
        {
            GuardKind value = values.Count == 0 ? GuardKind.Success : values.Dequeue();
            return value switch
            {
                GuardKind.Success => new([], null),
                GuardKind.Conflict => new(["vendor"], null),
                GuardKind.DetectionFailure => new([], "failed"),
                _ => throw new InvalidOperationException("guard")
            };
        }
    }

    private sealed class CallbackGuard(Action callback) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() { callback(); return new([], null); }
    }

    private sealed record IoCall(uint ControlCode, byte[] Buffer, CancellationToken Caller, CancellationToken Deadline);

    private sealed class ScriptedIo(params Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>>[] script) : IHidFeatureIo
    {
        private readonly Queue<Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>>> values = new(script);
        private readonly TaskCompletionSource<bool> called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IoCall> Calls { get; } = [];
        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint controlCode, byte[] buffer, CancellationToken callerCancellation, CancellationToken deadlineCancellation)
        {
            Calls.Add(new(controlCode, buffer, callerCancellation, deadlineCancellation));
            called.TrySetResult(true);
            if (values.Count == 0)
            {
                throw new InvalidOperationException("Unexpected I/O");
            }

            return values.Dequeue()(handle, controlCode, buffer, callerCancellation, deadlineCancellation);
        }
        public async Task WaitForCallsAsync(int count)
        {
            while (Calls.Count < count)
            {
                await called.Task;
                if (Calls.Count < count)
                {
                    await Task.Yield();
                }
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly SemaphoreSlim timerCreated = new(0);
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => utcNow.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer;
            lock (sync)
            {
                timer = new Timer(this, callback, state, utcNow + dueTime);
                timers.Add(timer);
            }
            timerCreated.Release();
            return timer;
        }
        public async Task WaitForTimerCountAsync(
            int count,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (sync)
                {
                    if (timers.Count >= count)
                    {
                        return;
                    }
                }

                await timerCreated.WaitAsync(cancellationToken);
            }
        }
        public void Advance(TimeSpan amount)
        {
            List<Timer> due;
            lock (sync) { utcNow += amount; due = timers.Where(t => !t.Disposed && t.Due <= utcNow).ToList(); }
            foreach (Timer timer in due)
            {
                timer.Fire();
            }
        }
        private sealed class Timer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = due;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = owner.utcNow + dueTime; return true; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void Fire() { if (!Disposed) { Disposed = true; callback(state); } }
        }
    }
}
