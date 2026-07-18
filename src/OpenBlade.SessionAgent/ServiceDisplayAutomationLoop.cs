using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public sealed class ServiceDisplayAutomationLoop
{
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
}
