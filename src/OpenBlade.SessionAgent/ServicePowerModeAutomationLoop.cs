using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public sealed class ServicePowerModeAutomationLoop
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly WindowsPowerModeAutomationCoordinator coordinator;

    public ServicePowerModeAutomationLoop(
        IServiceStatusWatcher statusWatcher,
        WindowsPowerModeAutomationCoordinator coordinator)
    {
        this.statusWatcher = statusWatcher
            ?? throw new ArgumentNullException(nameof(statusWatcher));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await foreach (ServiceWatchUpdate update in statusWatcher
                    .WatchStatusAsync(cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    switch (update)
                    {
                        case ServiceWatchUpdate.Connected connected:
                            coordinator.Observe(connected.Status);
                            break;
                        case ServiceWatchUpdate.Reconnecting:
                            coordinator.MarkUnavailable();
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                coordinator.MarkUnavailable();
                await Task.Delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
