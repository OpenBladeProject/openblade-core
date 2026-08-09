namespace OpenBlade.Tray.Tests;

public sealed class ServiceShutdownConfirmationTests
{
    [Fact]
    public async Task WaitForStoppedAsyncRunningThenStopPendingThenStoppedSucceeds()
    {
        var states = new Queue<ObservedServiceShutdownState>(
        [
            ObservedServiceShutdownState.Running,
            ObservedServiceShutdownState.StopPending,
            ObservedServiceShutdownState.Stopped,
        ]);
        int delays = 0;
        var confirmation = new ServiceShutdownConfirmation(
            states.Dequeue,
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays++;
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1));

        bool stopped = await confirmation.WaitForStoppedAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(2, delays);
        Assert.Empty(states);
    }

    [Theory]
    [InlineData(ObservedServiceShutdownState.Running)]
    [InlineData(ObservedServiceShutdownState.StopPending)]
    public async Task WaitForStoppedAsyncNonStoppedStateTimesOutClosed(
        ObservedServiceShutdownState state)
    {
        int reads = 0;
        int delays = 0;
        var confirmation = new ServiceShutdownConfirmation(
            () =>
            {
                reads++;
                return state;
            },
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1));

        bool stopped = await confirmation.WaitForStoppedAsync(CancellationToken.None);

        Assert.False(stopped);
        Assert.Equal(3, reads);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task WaitForStoppedAsyncCancellationStopsPolling()
    {
        int reads = 0;
        using var cancellation = new CancellationTokenSource();
        var confirmation = new ServiceShutdownConfirmation(
            () =>
            {
                reads++;
                return ObservedServiceShutdownState.Running;
            },
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => confirmation.WaitForStoppedAsync(cancellation.Token));

        Assert.Equal(1, reads);
    }
}
