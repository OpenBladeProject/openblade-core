using System.Collections.Concurrent;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class BoundedEnumActionPumpTests
{
    [Fact]
    public async Task ExecutesSequentiallyInOrderAndRejectsWhenPendingQueueIsFull()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<FunctionRowAction>();
        int active = 0;
        int maximumActive = 0;

        await using var pump = new BoundedEnumActionPump<FunctionRowAction>(
            async (action, token) =>
            {
                int nowActive = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, nowActive);
                executed.Enqueue(action);
                try
                {
                    if (action == FunctionRowAction.VolumeMute)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(token);
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref active) == 0
                        && executed.Count == 3)
                    {
                        allFinished.TrySetResult();
                    }
                }
            },
            capacity: 2);

        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeMute));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeDown));
        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeUp));
        Assert.False(pump.TryEnqueue(FunctionRowAction.Project));

        releaseFirst.TrySetResult();
        await allFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            [
                FunctionRowAction.VolumeMute,
                FunctionRowAction.VolumeDown,
                FunctionRowAction.VolumeUp,
            ],
            executed.ToArray());
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task ReportsExecutionFailureAndContinuesProcessing()
    {
        var resumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<Exception>();
        await using var pump = new BoundedEnumActionPump<FunctionRowAction>(
            (action, _) =>
            {
                if (action == FunctionRowAction.VolumeMute)
                {
                    throw new InvalidOperationException("input injection failed");
                }

                resumed.TrySetResult();
                return Task.CompletedTask;
            },
            errorHandler: failures.Enqueue);

        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeMute));
        await WaitUntilAsync(() => failures.Count == 1);
        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeDown));
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        InvalidOperationException failure =
            Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        Assert.Equal("input injection failed", failure.Message);
    }

    [Fact]
    public async Task ThrowingErrorHandlerDoesNotStopWorker()
    {
        var resumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        await using var pump = new BoundedEnumActionPump<FunctionRowAction>(
            (action, _) =>
            {
                Interlocked.Increment(ref attempts);
                if (action == FunctionRowAction.VolumeMute)
                {
                    throw new InvalidOperationException("input injection failed");
                }

                resumed.TrySetResult();
                return Task.CompletedTask;
            },
            errorHandler: _ => throw new InvalidOperationException("logger failed"));

        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeMute));
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1);
        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeDown));
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task RejectsUndefinedActionsWithoutConsumingCapacity()
    {
        var delivered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new BoundedEnumActionPump<FunctionRowAction>(
            (action, _) =>
            {
                Assert.Equal(FunctionRowAction.PrintScreen, action);
                delivered.TrySetResult();
                return Task.CompletedTask;
            },
            capacity: 1);

        Assert.False(pump.TryEnqueue((FunctionRowAction)999));
        Assert.True(pump.TryEnqueue(FunctionRowAction.PrintScreen));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SupportsSpecialKeySlotValues()
    {
        var delivered = new TaskCompletionSource<SpecialKeySlot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new BoundedEnumActionPump<SpecialKeySlot>(
            (slot, _) =>
            {
                delivered.TrySetResult(slot);
                return Task.CompletedTask;
            });

        Assert.False(pump.TryEnqueue((SpecialKeySlot)999));
        Assert.True(pump.TryEnqueue(SpecialKeySlot.M3));
        Assert.Equal(
            SpecialKeySlot.M3,
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DisposeCancelsInFlightActionRejectsNewWorkAndIsIdempotent()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new BoundedEnumActionPump<FunctionRowAction>(
            async (_, token) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });

        Assert.True(pump.TryEnqueue(FunctionRowAction.VolumeMute));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await pump.DisposeAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(pump.TryEnqueue(FunctionRowAction.VolumeDown));
        await pump.DisposeAsync();
    }

}
