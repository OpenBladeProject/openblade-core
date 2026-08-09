namespace OpenBlade.Tray;

internal enum TrayCompleteShutdownResult
{
    Completed,
    Canceled,
    Unconfirmed,
}

/// <summary>
/// Coordinates UI-affine tray teardown. Its awaits intentionally capture the
/// caller's synchronization context so Session Agent signaling and tray exit
/// resume on the WinUI thread. Do not add ConfigureAwait(false) here.
/// </summary>
internal sealed class TrayCompleteShutdownCoordinator
{
    private readonly Func<CancellationToken, Task<ElevatedServiceShutdownOutcome>> requestAsync;
    private readonly Func<CancellationToken, Task<bool>> confirmStoppedAsync;
    private readonly Action stopSessionAgent;
    private readonly Func<Task> exitTrayAsync;

    public TrayCompleteShutdownCoordinator(
        Func<CancellationToken, Task<ElevatedServiceShutdownOutcome>> requestAsync,
        Func<CancellationToken, Task<bool>> confirmStoppedAsync,
        Action stopSessionAgent,
        Func<Task> exitTrayAsync)
    {
        this.requestAsync = requestAsync
            ?? throw new ArgumentNullException(nameof(requestAsync));
        this.confirmStoppedAsync = confirmStoppedAsync
            ?? throw new ArgumentNullException(nameof(confirmStoppedAsync));
        this.stopSessionAgent = stopSessionAgent
            ?? throw new ArgumentNullException(nameof(stopSessionAgent));
        this.exitTrayAsync = exitTrayAsync
            ?? throw new ArgumentNullException(nameof(exitTrayAsync));
    }

    public async Task<TrayCompleteShutdownResult> ShutdownAsync(
        CancellationToken cancellationToken)
    {
        ElevatedServiceShutdownOutcome outcome = await requestAsync(cancellationToken);
        if (outcome == ElevatedServiceShutdownOutcome.Canceled)
        {
            return TrayCompleteShutdownResult.Canceled;
        }

        if (outcome == ElevatedServiceShutdownOutcome.Indeterminate
            && !await confirmStoppedAsync(cancellationToken))
        {
            return TrayCompleteShutdownResult.Unconfirmed;
        }

        stopSessionAgent();
        await exitTrayAsync();
        return TrayCompleteShutdownResult.Completed;
    }
}
