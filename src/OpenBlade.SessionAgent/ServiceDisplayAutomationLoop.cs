using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public sealed class ServiceDisplayAutomationLoop
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);
    private readonly IServiceClient serviceClient;
    private readonly DisplayAutomationCoordinator coordinator;

    public ServiceDisplayAutomationLoop(
        IServiceClient serviceClient,
        DisplayAutomationCoordinator coordinator)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await foreach (ServiceWatchUpdate update in serviceClient
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
