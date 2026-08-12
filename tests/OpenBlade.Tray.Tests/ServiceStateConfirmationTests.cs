namespace OpenBlade.Tray.Tests;

public sealed class ServiceStateConfirmationTests
{
    [Fact]
    public async Task WaitForAsyncStartPendingThenRunningSucceeds()
    {
        var states = new Queue<ObservedServiceState>(
        [
            ObservedServiceState.StartPending,
            ObservedServiceState.Running,
        ]);
        int delays = 0;
        var confirmation = new ServiceStateConfirmation(
            states.Dequeue,
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays++;
                return Task.CompletedTask;
            },
            maximumPolls: 2,
            TimeSpan.FromMilliseconds(1));

        bool running = await confirmation.WaitForAsync(
            ObservedServiceState.Running,
            CancellationToken.None);

        Assert.True(running);
        Assert.Equal(1, delays);
        Assert.Empty(states);
    }

    [Fact]
    public async Task WaitForAsyncRunningThenStopPendingThenStoppedSucceeds()
    {
        var states = new Queue<ObservedServiceState>(
        [
            ObservedServiceState.Running,
            ObservedServiceState.StopPending,
            ObservedServiceState.Stopped,
        ]);
        int delays = 0;
        var confirmation = new ServiceStateConfirmation(
            states.Dequeue,
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays++;
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1));

        bool stopped = await confirmation.WaitForAsync(
            ObservedServiceState.Stopped,
            CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(2, delays);
        Assert.Empty(states);
    }

    [Fact]
    public async Task WaitForAsyncNonTerminalStateTimesOutClosed()
    {
        int reads = 0;
        int delays = 0;
        var confirmation = new ServiceStateConfirmation(
            () =>
            {
                reads++;
                return ObservedServiceState.StopPending;
            },
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1));

        bool running = await confirmation.WaitForAsync(
            ObservedServiceState.Running,
            CancellationToken.None);

        Assert.False(running);
        Assert.Equal(3, reads);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task WaitForAsyncUnavailableStateFailsClosedImmediately()
    {
        int reads = 0;
        int delays = 0;
        var confirmation = new ServiceStateConfirmation(
            () =>
            {
                reads++;
                return ObservedServiceState.Unavailable;
            },
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1));

        bool running = await confirmation.WaitForAsync(
            ObservedServiceState.Running,
            CancellationToken.None);

        Assert.False(running);
        Assert.Equal(1, reads);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task WaitForAsyncCancellationStopsPolling()
    {
        int reads = 0;
        using var cancellation = new CancellationTokenSource();
        var confirmation = new ServiceStateConfirmation(
            () =>
            {
                reads++;
                return ObservedServiceState.StartPending;
            },
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => confirmation.WaitForAsync(
                ObservedServiceState.Running,
                cancellation.Token));

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task WaitForAsyncRejectsNonTerminalExpectedState()
    {
        var confirmation = new ServiceStateConfirmation(
            () => ObservedServiceState.Stopped,
            (_, _) => Task.CompletedTask,
            maximumPolls: 1,
            TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => confirmation.WaitForAsync(
                ObservedServiceState.StopPending,
                CancellationToken.None));
    }
}
