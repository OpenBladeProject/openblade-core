using System.Collections.Concurrent;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class FunctionRowInputPumpTests
{
    [Fact]
    public async Task QueuePressureDropsRepeatButNeverRelease()
    {
        var delivered = new ConcurrentQueue<FunctionRowInputEvent>();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new FunctionRowInputPump(
            async (inputEvent, _) =>
            {
                delivered.Enqueue(inputEvent);
                if (inputEvent.Phase == FunctionRowInputPhase.Down)
                {
                    firstStarted.TrySetResult();
                    await continueFirst.Task;
                }
            },
            _ => Task.CompletedTask,
            capacity: 1);
        FunctionRowInputEvent down = Input(FunctionRowInputPhase.Down);
        FunctionRowInputEvent repeat = Input(FunctionRowInputPhase.Repeat);
        FunctionRowInputEvent up = Input(FunctionRowInputPhase.Up);

        Assert.True(pump.TryEnqueue(down));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.TryEnqueue(repeat));
        Assert.True(pump.TryEnqueue(repeat));
        Assert.True(pump.TryEnqueue(up));
        continueFirst.TrySetResult();

        await WaitUntilAsync(() => delivered.Count == 2);
        Assert.Equal([down, up], delivered);
    }

    [Fact]
    public async Task ResetClearsPendingEdgesAndRunsAfterInFlightWork()
    {
        var operations = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new FunctionRowInputPump(
            async (inputEvent, _) =>
            {
                operations.Enqueue(inputEvent.Phase.ToString());
                firstStarted.TrySetResult();
                await continueFirst.Task;
            },
            _ =>
            {
                operations.Enqueue("Reset");
                return Task.CompletedTask;
            });

        Assert.True(pump.TryEnqueue(Input(FunctionRowInputPhase.Down)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.TryEnqueue(Input(FunctionRowInputPhase.Repeat)));
        Assert.True(pump.TryEnqueue(Input(FunctionRowInputPhase.Up)));
        pump.Reset();
        continueFirst.TrySetResult();

        await WaitUntilAsync(() => operations.Count == 2);
        Assert.Equal(["Down", "Reset"], operations);
    }

    [Fact]
    public async Task ResetAfterDispose_IsNoOp()
    {
        var pump = new FunctionRowInputPump(
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask);

        await pump.DisposeAsync();

        pump.Reset();
        Assert.False(pump.TryEnqueue(Input(FunctionRowInputPhase.Down)));
    }

    [Fact]
    public async Task ResetWhileDisposeIsDraining_IsNoOp()
    {
        var resetStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReset = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new FunctionRowInputPump(
            (_, _) => Task.CompletedTask,
            async _ =>
            {
                resetStarted.TrySetResult();
                await continueReset.Task;
            });

        Task dispose = pump.DisposeAsync().AsTask();
        await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pump.Reset();
        continueReset.TrySetResult();
        await dispose;
    }

    private static FunctionRowInputEvent Input(FunctionRowInputPhase phase) => new(
        0x43,
        FunctionRowAction.DisplayBrightnessUp,
        phase);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
