using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PerformanceShortcutTriggerPumpTests
{
    [Fact]
    public async Task PreservesShortcutOrderAndRejectsUnknownValues()
    {
        var sent = new ConcurrentQueue<PerformanceShortcutSource>();
        await using var pump = new PerformanceShortcutTriggerPump(
            (source, _) =>
            {
                sent.Enqueue(source);
                return Task.CompletedTask;
            });

        Assert.True(pump.TryEnqueue(PerformanceShortcutSource.DedicatedButton));
        Assert.True(pump.TryEnqueue(PerformanceShortcutSource.FunctionP));
        Assert.False(pump.TryEnqueue((PerformanceShortcutSource)999));
        await WaitUntilAsync(() => sent.Count == 2);

        Assert.Equal(
            [
                PerformanceShortcutSource.DedicatedButton,
                PerformanceShortcutSource.FunctionP,
            ],
            sent.ToArray());
    }

    [Fact]
    public async Task ClearPendingDoesNotCancelAnInFlightShortcut()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<PerformanceShortcutSource>();
        await using var pump = new PerformanceShortcutTriggerPump(
            async (source, token) =>
            {
                sent.Enqueue(source);
                firstStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            });

        Assert.True(pump.TryEnqueue(PerformanceShortcutSource.DedicatedButton));
        Assert.True(pump.TryEnqueue(PerformanceShortcutSource.FunctionP));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pump.ClearPending();
        release.TrySetResult();
        await Task.Delay(25);

        Assert.Equal([PerformanceShortcutSource.DedicatedButton], sent.ToArray());
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
