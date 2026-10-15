using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090581CommandSessionGateTests
{
    [Fact]
    public async Task EarlyManagedFailureBlocksAnotherSessionUntilNativeRetires()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new StubSession(new(default, HidSessionFailureKind.Timeout)
        {
            NativeRetirement = retirement.Task,
        });
        var next = new StubSession(new(ReadOnlyMemory<byte>.Empty, null));
        var firstGated = new Rz090581CommandSessionGate(first, gate);
        var nextGated = new Rz090581CommandSessionGate(next, gate);

        Assert.Equal(HidSessionFailureKind.Timeout, (await Execute(firstGated)).Failure);
        Assert.Equal(HidSessionFailureKind.Exhausted, (await Execute(nextGated, 25)).Failure);
        Assert.Equal(0, next.Calls);
        await firstGated.DisposeAsync();
        Assert.Equal(0, next.Calls);

        retirement.SetResult();
        Assert.Null((await Execute(nextGated)).Failure);
        Assert.Equal(1, next.Calls);
    }

    [Fact]
    public async Task FailedRetirementLeavesOtherSessionsBlockedWithBoundedAdmission()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retirement.SetException(new IOException("Native retirement was not confirmed."));
        var first = new StubSession(new(default, HidSessionFailureKind.NativeIoFailure)
        {
            NativeRetirement = retirement.Task,
        });
        var next = new StubSession(new(ReadOnlyMemory<byte>.Empty, null));

        await Execute(new Rz090581CommandSessionGate(first, gate));
        Assert.Equal(HidSessionFailureKind.Exhausted,
            (await Execute(new Rz090581CommandSessionGate(next, gate), 25)).Failure);
        Assert.Equal(0, next.Calls);
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingSubmitsNothingAndDoesNotReleaseOwnersGate()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var inner = new StubSession(new(ReadOnlyMemory<byte>.Empty, null));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = new Rz090581CommandSessionGate(inner, gate);

        var result = await session.ExecuteAsync(new(0x0D, 0x02, [], 0),
            static () => TransactionIdAssignment.Valid(1), TimeSpan.FromSeconds(1), cancellation.Token);

        Assert.Equal(HidSessionFailureKind.QueuedCanceled, result.Failure);
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, gate.CurrentCount);
    }

    private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Execute(
        IRazerCommandSession session, int milliseconds = 1000) => session.ExecuteAsync(
            new(0x0D, 0x02, [], 0), static () => TransactionIdAssignment.Valid(1),
            TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None);

    private sealed class StubSession(HidSessionResult<ReadOnlyMemory<byte>> result) : IRazerCommandSession
    {
        public int Calls { get; private set; }
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command, Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
