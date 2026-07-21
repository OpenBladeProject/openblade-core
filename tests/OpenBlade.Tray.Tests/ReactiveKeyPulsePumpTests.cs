using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ReactiveKeyPulsePumpTests
{
    [Fact]
    public async Task Pump_IsSequentialBoundedAndPreservesRepeatedKeyPresses()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<byte>();
        int active = 0;
        int maximumActive = 0;

        await using var pump = new ReactiveKeyPulsePump(
            async (pulse, token) =>
            {
                int nowActive = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, nowActive);
                lock (sent)
                {
                    sent.Add(pulse.MatrixSlot);
                    if (sent.Count == 4)
                    {
                        allSent.TrySetResult();
                    }
                }

                try
                {
                    if (pulse.MatrixSlot == 19)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(token);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            capacity: 3);

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(19)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(53)));
        Assert.False(pump.TryEnqueue(new ReactiveKeyPulse(71)));

        releaseFirst.TrySetResult();
        await allSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([19, 36, 36, 53], sent);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task Pump_PreservesPendingPulseAcrossTransientDelay()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<byte>();
        await using var pump = new ReactiveKeyPulsePump(
            async (pulse, token) =>
            {
                lock (sent)
                {
                    sent.Add(pulse.MatrixSlot);
                }

                if (pulse.MatrixSlot == 19)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    firstFinished.TrySetResult();
                }
                else if (pulse.MatrixSlot == 36)
                {
                    delayedSent.TrySetResult();
                }
            });

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(19)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        await Task.Delay(150);
        releaseFirst.TrySetResult();
        await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await delayedSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (sent)
        {
            Assert.Equal([19, 36], sent);
        }
    }

    [Fact]
    public async Task Pump_DoesNotCancelAcceptedPulseDuringTransientTransportDelay()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<byte>();
        await using var pump = new ReactiveKeyPulsePump(
            async (pulse, token) =>
            {
                if (pulse.MatrixSlot == 19)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                }

                lock (sent)
                {
                    sent.Add(pulse.MatrixSlot);
                    if (sent.Count == 2)
                    {
                        allSent.TrySetResult();
                    }
                }
            });

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(19)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        await Task.Delay(150);
        releaseFirst.TrySetResult();
        await allSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (sent)
        {
            Assert.Equal([19, 36], sent);
        }
    }

    [Fact]
    public async Task Pump_RetriesAcceptedPulseAfterTransientSendFailure()
    {
        var delivered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var failures = new List<Exception>();
        await using var pump = new ReactiveKeyPulsePump(
            (pulse, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new IOException("pipe reconnecting");
                }

                Assert.Equal(53, pulse.MatrixSlot);
                delivered.TrySetResult();
                return Task.CompletedTask;
            },
            errorHandler: failures.Add,
            retryDelay: TimeSpan.Zero);

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(53)));

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Pump_ReportsOnceAfterBoundedRetryExhaustion()
    {
        var attemptsFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var failures = new List<Exception>();
        await using var pump = new ReactiveKeyPulsePump(
            (_, _) =>
            {
                attempts++;
                if (attempts == 3)
                {
                    attemptsFinished.TrySetResult();
                }

                throw new InvalidOperationException("renderer inactive");
            },
            errorHandler: failures.Add,
            retryDelay: TimeSpan.Zero);

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(71)));

        await attemptsFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => failures.Count == 1);
        Assert.Equal(3, attempts);
        Assert.Single(failures);
    }

    [Fact]
    public async Task ThrowingErrorHandler_DoesNotStopWorker()
    {
        var resumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        await using var pump = new ReactiveKeyPulsePump(
            (pulse, _) =>
            {
                attempts++;
                if (pulse.MatrixSlot == 19)
                {
                    throw new InvalidOperationException("renderer inactive");
                }

                Assert.Equal(36, pulse.MatrixSlot);
                resumed.TrySetResult();
                return Task.CompletedTask;
            },
            errorHandler: _ => throw new InvalidOperationException("logger unavailable"),
            maxDeliveryAttempts: 1,
            retryDelay: TimeSpan.Zero);

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(19)));
        await WaitUntilAsync(() => attempts == 1);
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));

        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Dispose_CancelsDeliveryAndRejectsNewPulses()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new ReactiveKeyPulsePump(
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(19)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await pump.DisposeAsync();

        Assert.False(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        await pump.DisposeAsync();
    }

    [Fact]
    public async Task ClearPending_DropsQueuedPulsesAndContinuesAccepting()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<byte>();

        await using var pump = new ReactiveKeyPulsePump(
            async (pulse, token) =>
            {
                lock (sent)
                {
                    sent.Add(pulse.MatrixSlot);
                }

                if (pulse.MatrixSlot == 19)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    firstFinished.TrySetResult();
                }
                else if (pulse.MatrixSlot == 71)
                {
                    resumedSent.TrySetResult();
                }
            });

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(19)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(53)));

        pump.ClearPending();
        releaseFirst.TrySetResult();
        await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        lock (sent)
        {
            Assert.Equal([19], sent);
        }

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(71)));
        await resumedSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (sent)
        {
            Assert.Equal([19, 71], sent);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
