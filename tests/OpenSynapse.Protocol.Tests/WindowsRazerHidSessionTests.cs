using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class WindowsRazerHidSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
    public async Task QueuedCancellation_WinsBeforeClaimAndConsumesNoId()
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
        Assert.Equal(0, assignments);
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

        Assert.Equal(expected, (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default)).Failure);
        Assert.Null((await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
        Assert.Equal(2, io.Calls.Count);
    }

    [Fact]
    public async Task ExchangeUsesExactCodesFreshBuffersAndGetRequiresNinetyOneBytes()
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
        WindowsRazerHidSession shortActor = CreateActor(shortIo);
        Assert.Equal(HidSessionFailureKind.NativeIoFailure, (await shortActor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
        await shortActor.DisposeAsync();
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

        Assert.Equal(expected, (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default)).Failure);
        Assert.Contains(
            (await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure,
            new HidSessionFailureKind?[] { HidSessionFailureKind.Poisoned, HidSessionFailureKind.Disposed });
        await actor.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TaskSevenFaultMapsNativeFailureAndPoisons(bool faultSet)
    {
        Func<OpenedHidHandle, uint, byte[], CancellationToken, CancellationToken, Task<HidFeatureIoResult>> fault =
            (_, _, _, _, _) => Task.FromException<HidFeatureIoResult>(new IOException("native"));
        var io = faultSet
            ? new ScriptedIo(fault)
            : new ScriptedIo(SetSuccess(), fault);
        WindowsRazerHidSession actor = CreateActor(io);

        Assert.Equal(
            HidSessionFailureKind.NativeIoFailure,
            (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default)).Failure);
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
    public async Task PoisonRejectsAdmissionWhileAutomaticCleanupIsDrainingThenBecomesDisposed()
    {
        byte[] malformed = Response(1, 1, 1, []);
        malformed[88] ^= 1;
        var enteredDisposal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDisposal = new ManualResetEventSlim();
        var io = new ScriptedIo(SetSuccess(), GetResult(malformed));
        WindowsRazerHidSession actor = CreateActor(
            io,
            disposeBound: () =>
            {
                enteredDisposal.TrySetResult(true);
                releaseDisposal.Wait();
            });

        Assert.Equal(
            HidSessionFailureKind.ProtocolFailure,
            (await actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default)).Failure);
        await enteredDisposal.Task;
        Assert.Equal(
            HidSessionFailureKind.Poisoned,
            (await actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default)).Failure);
        releaseDisposal.Set();
        await actor.DisposeAsync();
        Assert.Equal(
            HidSessionFailureKind.Disposed,
            (await actor.ExecuteAsync(new(1, 3, [], 0), () => TransactionIdAssignment.Valid(3), Timeout, default)).Failure);
    }

    [Fact]
    public async Task MalformedResponsePoisonsBeforeQueuedRequestAdvances()
    {
        byte[] malformed = Response(1, 1, 1, []);
        malformed[88] ^= 1;
        var io = new ScriptedIo(SetSuccess(), GetResult(malformed), SetSuccess());
        WindowsRazerHidSession actor = CreateActor(io);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> first = actor.ExecuteAsync(new(1, 1, [], 0), () => TransactionIdAssignment.Valid(1), Timeout, default);
        Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.ExecuteAsync(new(1, 2, [], 0), () => TransactionIdAssignment.Valid(2), Timeout, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await first).Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, (await queued).Failure);
        Assert.Equal(2, io.Calls.Count);
        await actor.DisposeAsync();
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
        WindowsRazerHidSession actor = new(handle, io, new ScriptedGuard(), TimeProvider.System, lease);
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
        Assert.Throws<ArgumentNullException>(() => new WindowsRazerHidSession(handle, null!, new ScriptedGuard(), TimeProvider.System, lease));
        Assert.Empty(events);

        await handle.DisposeAsync();
        lease.Dispose();
        Assert.Equal(["bound", "file"], events);
    }

    private const uint SetCode = 0x000B0191;
    private const uint GetCode = 0x000B0192;

    private static WindowsRazerHidSession CreateActor(ScriptedIo io, IHardwareAccessGuard? guard = null, TimeProvider? time = null, List<string>? events = null, Action? disposeBound = null)
    {
        (OpenedHidHandle handle, ValidationRunContext.ActorLease lease) = Resources(events, disposeBound);
        return new WindowsRazerHidSession(handle, io, guard ?? new ScriptedGuard(), time ?? TimeProvider.System, lease);
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
        for (int i = 2; i <= 87; i++) checksum ^= report[i];
        report[88] = checksum;
        byte[] feature = new byte[91];
        report.CopyTo(feature, 1);
        return feature;
    }

    private static void Rechecksum(byte[] feature)
    {
        byte checksum = 0;
        for (int i = 3; i <= 88; i++) checksum ^= feature[i];
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
            if (values.Count == 0) throw new InvalidOperationException("Unexpected I/O");
            return values.Dequeue()(handle, controlCode, buffer, callerCancellation, deadlineCancellation);
        }
        public async Task WaitForCallsAsync(int count)
        {
            while (Calls.Count < count)
            {
                await called.Task;
                if (Calls.Count < count) await Task.Yield();
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => utcNow.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, utcNow + dueTime);
            lock (sync) timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            List<Timer> due;
            lock (sync) { utcNow += amount; due = timers.Where(t => !t.Disposed && t.Due <= utcNow).ToList(); }
            foreach (Timer timer in due) timer.Fire();
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
