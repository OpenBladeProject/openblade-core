using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class RazerCommandSessionGateTests
{
    [Theory]
    [InlineData(CommandPath.Execute)]
    [InlineData(CommandPath.Submit)]
    [InlineData(CommandPath.SubmitBatch)]
    public async Task EarlyManagedFailureBlocksAnotherSessionUntilNativeRetires(
        CommandPath path)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var retirement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)
        {
            NativeRetirement = retirement.Task,
        });
        var next = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty, null));
        var firstGated = new RazerCommandSessionGate(first, gate);
        var nextGated = new RazerCommandSessionGate(next, gate);

        Assert.Equal(
            HidSessionFailureKind.Timeout,
            (await InvokeAsync(firstGated, path)).Failure);
        Assert.Equal(
            HidSessionFailureKind.Exhausted,
            (await InvokeAsync(nextGated, path, 25)).Failure);
        Assert.Equal(0, next.Calls);

        await firstGated.DisposeAsync();
        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(0, gate.CurrentCount);

        retirement.SetResult();
        Assert.Null((await InvokeAsync(nextGated, path)).Failure);
        Assert.Equal(1, next.Calls);
    }

    [Theory]
    [InlineData(CommandPath.Execute)]
    [InlineData(CommandPath.Submit)]
    [InlineData(CommandPath.SubmitBatch)]
    public async Task FailedRetirementLeavesOtherSessionsBlockedWithBoundedAdmission(
        CommandPath path)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var retirement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        retirement.SetException(new IOException("Native retirement was not confirmed."));
        var first = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.NativeIoFailure)
        {
            NativeRetirement = retirement.Task,
        });
        var next = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty, null));

        await InvokeAsync(new RazerCommandSessionGate(first, gate), path);
        Assert.Equal(
            HidSessionFailureKind.Exhausted,
            (await InvokeAsync(
                new RazerCommandSessionGate(next, gate),
                path,
                25)).Failure);
        Assert.Equal(0, next.Calls);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Theory]
    [InlineData(CommandPath.Execute)]
    [InlineData(CommandPath.Submit)]
    [InlineData(CommandPath.SubmitBatch)]
    public async Task FinalizationExceptionBlocksAnotherSessionUntilNativeRetires(
        CommandPath path)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var retirement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalization = new NativeOperationFinalizationException(
            new HidFeatureIoResult(
                new NativeIoCompletion(false, 17, 31),
                true,
                HidFeatureTermination.Completed)
            {
                NativeRetirement = retirement.Task,
            },
            new InvalidOperationException("Native resource cleanup failed."));
        var first = new StubSession(finalization);
        var next = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty, null));

        NativeOperationFinalizationException thrown =
            await Assert.ThrowsAsync<NativeOperationFinalizationException>(
                () => InvokeAsync(new RazerCommandSessionGate(first, gate), path));
        Assert.Same(finalization, thrown);
        Assert.Equal(
            HidSessionFailureKind.Exhausted,
            (await InvokeAsync(
                new RazerCommandSessionGate(next, gate),
                path,
                25)).Failure);
        Assert.Equal(0, next.Calls);

        retirement.SetResult();
        Assert.Null((await InvokeAsync(
            new RazerCommandSessionGate(next, gate),
            path)).Failure);
        Assert.Equal(1, next.Calls);
    }

    [Theory]
    [InlineData(CommandPath.Execute)]
    [InlineData(CommandPath.Submit)]
    [InlineData(CommandPath.SubmitBatch)]
    public async Task CallerCancellationWhileWaitingSubmitsNothing(
        CommandPath path)
    {
        using var gate = new SemaphoreSlim(0, 1);
        var inner = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty, null));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HidSessionResult<ReadOnlyMemory<byte>> result = await InvokeAsync(
            new RazerCommandSessionGate(inner, gate),
            path,
            1000,
            cancellation.Token);

        Assert.Equal(HidSessionFailureKind.QueuedCanceled, result.Failure);
        Assert.Equal(
            HidSessionFailurePhase.SharedGateAdmission,
            result.Diagnostic?.Phase);
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Theory]
    [InlineData(CommandPath.Execute)]
    [InlineData(CommandPath.Submit)]
    [InlineData(CommandPath.SubmitBatch)]
    public async Task AdmissionTimeoutSubmitsNothing(CommandPath path)
    {
        using var gate = new SemaphoreSlim(0, 1);
        var inner = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty, null));

        HidSessionResult<ReadOnlyMemory<byte>> result = await InvokeAsync(
            new RazerCommandSessionGate(inner, gate),
            path,
            25);

        Assert.Equal(HidSessionFailureKind.Exhausted, result.Failure);
        Assert.Equal(
            HidSessionFailurePhase.SharedGateAdmission,
            result.Diagnostic?.Phase);
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task DisposeHonorsExplicitInnerOwnershipWithoutOwningTheSharedGate()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var actorOwned = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty, null));
        var gateOwned = new StubSession(new HidSessionResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty, null));

        await new RazerCommandSessionGate(actorOwned, gate).DisposeAsync();
        await new RazerCommandSessionGate(
            gateOwned,
            gate,
            ownsInner: true).DisposeAsync();

        Assert.Equal(0, actorOwned.DisposeCount);
        Assert.Equal(1, gateOwned.DisposeCount);
        Assert.Equal(0, gate.CurrentCount);
    }

    private static Task<HidSessionResult<ReadOnlyMemory<byte>>> InvokeAsync(
        RazerCommandSessionGate session,
        CommandPath path,
        int milliseconds = 1000,
        CancellationToken cancellationToken = default)
    {
        var command = new RazerCommandSpec(0x0D, 0x02, [], 0);
        Func<TransactionIdAssignment> assign =
            static () => TransactionIdAssignment.Valid(1);
        TimeSpan timeout = TimeSpan.FromMilliseconds(milliseconds);
        return path switch
        {
            CommandPath.Execute => session.ExecuteAsync(
                command,
                assign,
                timeout,
                cancellationToken),
            CommandPath.Submit => session.SubmitAsync(
                command,
                assign,
                timeout,
                cancellationToken),
            CommandPath.SubmitBatch => session.SubmitBatchAsync(
                [command],
                assign,
                timeout,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
    }

    public enum CommandPath
    {
        Execute,
        Submit,
        SubmitBatch,
    }

    private sealed class StubSession :
        IRazerCommandSession,
        IRazerSetFeatureCommandSession
    {
        private readonly HidSessionResult<ReadOnlyMemory<byte>> result;
        private readonly Exception? exception;

        internal StubSession(HidSessionResult<ReadOnlyMemory<byte>> result)
        {
            this.result = result;
        }

        internal StubSession(Exception exception)
        {
            this.exception = exception;
        }

        internal int Calls { get; private set; }
        internal int DisposeCount { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) => Complete();

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) => Complete();

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
            IReadOnlyList<RazerCommandSpec> commands,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) => Complete();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private Task<HidSessionResult<ReadOnlyMemory<byte>>> Complete()
        {
            Calls++;
            return exception is null
                ? Task.FromResult(result)
                : Task.FromException<HidSessionResult<ReadOnlyMemory<byte>>>(exception);
        }
    }
}
