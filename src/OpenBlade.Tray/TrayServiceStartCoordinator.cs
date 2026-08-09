namespace OpenBlade.Tray;

internal enum TrayServiceStartResult
{
    Completed,
    Canceled,
    Unconfirmed,
}

internal sealed class TrayServiceStartCoordinator
{
    private readonly Func<CancellationToken, Task<ElevatedServiceControlOutcome>> requestAsync;
    private readonly Func<CancellationToken, Task<bool>> confirmRunningAsync;

    public TrayServiceStartCoordinator(
        Func<CancellationToken, Task<ElevatedServiceControlOutcome>> requestAsync,
        Func<CancellationToken, Task<bool>> confirmRunningAsync)
    {
        this.requestAsync = requestAsync
            ?? throw new ArgumentNullException(nameof(requestAsync));
        this.confirmRunningAsync = confirmRunningAsync
            ?? throw new ArgumentNullException(nameof(confirmRunningAsync));
    }

    public async Task<TrayServiceStartResult> StartAsync(
        CancellationToken cancellationToken)
    {
        ElevatedServiceControlOutcome outcome = await requestAsync(cancellationToken)
            .ConfigureAwait(false);
        if (outcome == ElevatedServiceControlOutcome.Canceled)
        {
            return TrayServiceStartResult.Canceled;
        }
        if (outcome == ElevatedServiceControlOutcome.Indeterminate
            && !await confirmRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return TrayServiceStartResult.Unconfirmed;
        }

        return TrayServiceStartResult.Completed;
    }
}
