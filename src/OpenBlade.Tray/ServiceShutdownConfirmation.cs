namespace OpenBlade.Tray;

internal enum ObservedServiceShutdownState
{
    Running,
    StopPending,
    Stopped,
    Unavailable,
}

internal sealed class ServiceShutdownConfirmation
{
    private readonly Func<ObservedServiceShutdownState> readState;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly int maximumPolls;
    private readonly TimeSpan pollInterval;

    public ServiceShutdownConfirmation(
        Func<ObservedServiceShutdownState> readState,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        int maximumPolls,
        TimeSpan pollInterval)
    {
        this.readState = readState ?? throw new ArgumentNullException(nameof(readState));
        this.delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        this.maximumPolls = maximumPolls > 0
            ? maximumPolls
            : throw new ArgumentOutOfRangeException(nameof(maximumPolls));
        this.pollInterval = pollInterval > TimeSpan.Zero
            ? pollInterval
            : throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async Task<bool> WaitForStoppedAsync(CancellationToken cancellationToken)
    {
        for (int poll = 0; poll < maximumPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedServiceShutdownState state = readState();
            if (state == ObservedServiceShutdownState.Stopped)
            {
                return true;
            }
            if (state == ObservedServiceShutdownState.Unavailable)
            {
                return false;
            }

            if (poll + 1 < maximumPolls)
            {
                await delayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}
