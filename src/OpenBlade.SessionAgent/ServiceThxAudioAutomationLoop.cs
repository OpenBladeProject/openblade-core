using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed class ServiceThxAudioAutomationLoop(
    IServiceStatusWatcher statusWatcher,
    IThxForegroundSource foregroundSource,
    ThxAudioAutomationCoordinator coordinator)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task status = ObserveStatusAsync(lifetime.Token);
        Task foreground = ObserveForegroundAsync(lifetime.Token);
        try
        {
            await Task.WhenAny(status, foreground).ConfigureAwait(false);
        }
        finally
        {
            coordinator.MarkUnavailable();
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        try
        {
            await Task.WhenAll(status, foreground).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        // Losing the source disables only this optional feature; it must not
        // terminate input/display/lighting features supervised by Program.
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("THX application automation stopped: {0}", exception.Message);
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveStatusAsync(CancellationToken cancellationToken)
    {
        await foreach (ServiceWatchUpdate update in statusWatcher.WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (update is ServiceWatchUpdate.Connected connected)
            {
                coordinator.Observe(connected.Status);
            }
            else
            {
                coordinator.MarkUnavailable();
            }
        }
    }

    private async Task ObserveForegroundAsync(CancellationToken cancellationToken)
    {
        await foreach (ThxForegroundObservation observation in foregroundSource.WatchAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            coordinator.ObserveForeground(observation);
        }
    }
}
