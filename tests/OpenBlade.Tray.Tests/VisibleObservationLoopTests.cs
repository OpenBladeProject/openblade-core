using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class VisibleObservationLoopTests
{
    [Fact]
    public async Task StartObservesImmediatelyAndRepeatsAfterInjectedInterval()
    {
        var delay = new ManualDelay();
        int calls = 0;
        await using var loop = new VisibleObservationLoop(
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            delay.WaitAsync);

        loop.StartOrWake();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1 && delay.WaitingCount == 1);
        Assert.Equal(TimeSpan.FromSeconds(2), delay.LastDelay);

        delay.CompleteNext();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2 && delay.WaitingCount == 1);

        loop.Stop();
        delay.CompleteNext();
        await Task.Delay(20);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NextCycleUsesUpdatedInjectedInterval()
    {
        var delay = new ManualDelay();
        int calls = 0;
        TimeSpan interval = TimeSpan.FromSeconds(10);
        await using var loop = new VisibleObservationLoop(
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            delay.WaitAsync,
            observationIntervalProvider: () => interval);

        loop.StartOrWake();
        await WaitUntilAsync(() => calls == 1 && delay.WaitingCount == 1);
        Assert.Equal(TimeSpan.FromSeconds(10), delay.LastDelay);

        interval = TimeSpan.FromSeconds(1);
        loop.StartOrWake();
        await WaitUntilAsync(() => calls == 2 && delay.WaitingCount == 2);
        Assert.Equal(TimeSpan.FromSeconds(1), delay.LastDelay);
    }

    [Fact]
    public async Task InfiniteIntervalWaitsForExplicitWakeWithoutPeriodicPolling()
    {
        var delay = new ManualDelay();
        int calls = 0;
        await using var loop = new VisibleObservationLoop(
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            delay.WaitAsync,
            observationIntervalProvider: () => Timeout.InfiniteTimeSpan);

        loop.StartOrWake();
        await WaitUntilAsync(() => calls == 1 && delay.WaitingCount == 1);
        Assert.Equal(Timeout.InfiniteTimeSpan, delay.LastDelay);

        await Task.Delay(20);
        Assert.Equal(1, calls);

        loop.StartOrWake();
        await WaitUntilAsync(() => calls == 2);
    }

    [Fact]
    public async Task InactiveControlAccessSkipsObservationUntilAccessReturns()
    {
        var delay = new ManualDelay();
        bool canObserve = false;
        int calls = 0;
        await using var loop = new VisibleObservationLoop(
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            delay.WaitAsync,
            canObserve: () => canObserve);

        loop.StartOrWake();
        await WaitUntilAsync(() => delay.WaitingCount == 1);
        Assert.Equal(0, calls);

        canObserve = true;
        delay.CompleteNext();
        await WaitUntilAsync(() => calls == 1);
    }

    [Fact]
    public async Task WakeDuringObservationCoalescesOneNonOverlappingFollowUp()
    {
        var delay = new ManualDelay();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int active = 0;
        int maximumActive = 0;
        await using var loop = new VisibleObservationLoop(
            async token =>
            {
                int nowActive = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, nowActive);
                int call = Interlocked.Increment(ref calls);
                try
                {
                    if (call == 1)
                    {
                        await first.Task.WaitAsync(token);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            delay.WaitAsync);

        loop.StartOrWake();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        loop.StartOrWake();
        loop.StartOrWake();
        first.TrySetResult();

        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(1, maximumActive);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StopThenRapidRestartSerializesCancellationBeforeNewObservation()
    {
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int active = 0;
        int maximumActive = 0;
        await using var loop = new VisibleObservationLoop(
            async token =>
            {
                int nowActive = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, nowActive);
                int call = Interlocked.Increment(ref calls);
                try
                {
                    if (call == 1)
                    {
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        catch (OperationCanceledException)
                        {
                            firstCanceled.TrySetResult();
                            await releaseFirst.Task;
                            throw;
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        loop.StartOrWake();
        await WaitUntilAsync(() => calls == 1);
        loop.Stop();
        loop.StartOrWake();
        await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);

        releaseFirst.TrySetResult();
        await WaitUntilAsync(() => calls == 2);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task StopPreventsHiddenPollingErrorsContinueAndDisposeJoins()
    {
        var delay = new ManualDelay();
        var disposalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        int calls = 0;
        await using var loop = new VisibleObservationLoop(
            async token =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    throw new IOException("transient");
                }
                if (call == 2)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        await disposalRelease.Task;
                        throw;
                    }
                }
            },
            delay.WaitAsync,
            errors.Add);

        loop.StartOrWake();
        await WaitUntilAsync(() => calls == 1 && delay.WaitingCount == 1);
        delay.CompleteNext();
        await WaitUntilAsync(() => calls == 2);
        IOException error = Assert.IsType<IOException>(Assert.Single(errors));
        Assert.Equal("transient", error.Message);

        ValueTask disposal = loop.DisposeAsync();
        ValueTask concurrentDisposal = loop.DisposeAsync();
        Assert.False(disposal.IsCompleted);
        Assert.False(concurrentDisposal.IsCompleted);
        disposalRelease.TrySetResult();
        await Task.WhenAll(disposal.AsTask(), concurrentDisposal.AsTask());
        Assert.Equal(2, calls);

        loop.StartOrWake();
        await Task.Delay(20);
        Assert.Equal(2, calls);
    }

    private sealed class ManualDelay
    {
        private readonly object sync = new();
        private readonly Queue<TaskCompletionSource> waits = new();

        public TimeSpan LastDelay { get; private set; }

        public int WaitingCount
        {
            get { lock (sync) { return waits.Count; } }
        }

        public Task WaitAsync(TimeSpan delay, CancellationToken token)
        {
            LastDelay = delay;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            lock (sync)
            {
                waits.Enqueue(completion);
            }
            return completion.Task;
        }

        public void CompleteNext()
        {
            TaskCompletionSource completion;
            lock (sync)
            {
                completion = waits.Dequeue();
            }
            completion.TrySetResult();
        }
    }
}
