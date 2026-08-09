namespace OpenBlade.Tray;

internal enum ObservedServiceState
{
    Running,
    StartPending,
    ContinuePending,
    StopPending,
    Stopped,
    Other,
    Unavailable,
}

internal sealed class ServiceStateConfirmation
{
    private readonly Func<ObservedServiceState> readState;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly int maximumPolls;
    private readonly TimeSpan pollInterval;

    public ServiceStateConfirmation(
        Func<ObservedServiceState> readState,
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

    public async Task<bool> WaitForAsync(
        ObservedServiceState expectedState,
        CancellationToken cancellationToken)
    {
        if (expectedState is not ObservedServiceState.Running
            and not ObservedServiceState.Stopped)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedState));
        }

        for (int poll = 0; poll < maximumPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedServiceState state = readState();
            if (state == expectedState)
            {
                return true;
            }
            if (state == ObservedServiceState.Unavailable)
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
