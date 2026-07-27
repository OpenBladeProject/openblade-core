using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PerformanceShortcutTriggerPumpTests
{
    private static readonly TimeSpan TestQuietPeriod = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task BurstCoalescesToLastIntentAfterQuietPeriod()
    {
        var sent = new ConcurrentQueue<PerformanceShortcutIntent>();
        await using var pump = new PerformanceShortcutTriggerPump(
            (intent, _) =>
            {
                sent.Enqueue(intent);
                return Task.CompletedTask;
            },
            TestQuietPeriod);

        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Silent, 1)));
        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Performance, 2)));
        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Custom, 3)));
        Assert.False(pump.TryEnqueue(Intent(PerformancePreset.Custom, 0)));
        await WaitUntilAsync(() => sent.Count == 1);

        PerformanceShortcutIntent actual = Assert.Single(sent);
        Assert.Equal(PerformancePreset.Custom, actual.Target);
        Assert.Equal(3, actual.Generation);
    }

    [Fact]
    public async Task PressesDuringInFlightWriteCoalesceIntoOneFollowUp()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<PerformanceShortcutIntent>();
        int attempt = 0;
        await using var pump = new PerformanceShortcutTriggerPump(
            async (intent, token) =>
            {
                sent.Enqueue(intent);
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    firstStarted.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            },
            TestQuietPeriod);

        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Silent, 1)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Performance, 2)));
        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Custom, 3)));
        release.TrySetResult();
        await WaitUntilAsync(() => sent.Count == 2);

        Assert.Equal(
            [PerformancePreset.Silent, PerformancePreset.Custom],
            sent.Select(value => value.Target).ToArray());
    }

    [Fact]
    public async Task ClearPendingDoesNotCancelAnInFlightShortcut()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<PerformanceShortcutIntent>();
        await using var pump = new PerformanceShortcutTriggerPump(
            async (intent, token) =>
            {
                sent.Enqueue(intent);
                firstStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            TestQuietPeriod);

        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Silent, 1)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Performance, 2)));
        pump.ClearPending();
        release.TrySetResult();
        await Task.Delay(TestQuietPeriod + TimeSpan.FromMilliseconds(25));

        Assert.Equal([PerformancePreset.Silent], sent.Select(value => value.Target).ToArray());
    }

    [Fact]
    public async Task ThrowingErrorHandlerDoesNotStopWorker()
    {
        var sent = new ConcurrentQueue<PerformanceShortcutIntent>();
        int attempts = 0;
        await using var pump = new PerformanceShortcutTriggerPump(
            (intent, _) =>
            {
                attempts++;
                if (intent.Generation == 1)
                {
                    throw new InvalidOperationException("service unavailable");
                }

                sent.Enqueue(intent);
                return Task.CompletedTask;
            },
            TestQuietPeriod,
            _ => throw new InvalidOperationException("logger unavailable"));

        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Silent, 1)));
        await WaitUntilAsync(() => attempts == 1);
        Assert.True(pump.TryEnqueue(Intent(PerformancePreset.Performance, 2)));

        await WaitUntilAsync(() => sent.Count == 1);
        Assert.Equal(PerformancePreset.Performance, Assert.Single(sent).Target);
    }

    private static PerformanceShortcutIntent Intent(
        PerformancePreset target,
        long generation) => new(
            PerformanceShortcutSource.DedicatedButton,
            PowerSource.PluggedIn,
            target,
            generation);

}
