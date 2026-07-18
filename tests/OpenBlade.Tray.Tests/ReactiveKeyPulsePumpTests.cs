using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ReactiveKeyPulsePumpTests
{
    [Fact]
    public async Task Pump_IsSequentialBoundedAndPreservesRepeatedMakeEvents()
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
                    if (sent.Count == 3)
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
            capacity: 2);

        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(19)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        Assert.True(pump.TryEnqueue(new ReactiveKeyPulse(36)));
        Assert.False(pump.TryEnqueue(new ReactiveKeyPulse(53)));

        releaseFirst.TrySetResult();
        await allSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([19, 36, 36], sent);
        Assert.Equal(1, maximumActive);
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
}
