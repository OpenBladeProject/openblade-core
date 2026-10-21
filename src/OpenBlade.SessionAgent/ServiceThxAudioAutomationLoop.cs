using System.Runtime.ExceptionServices;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed class ServiceThxAudioAutomationLoop(
    IServiceStatusWatcher statusWatcher,
    IThxForegroundSource foregroundSource,
    ThxAudioAutomationCoordinator coordinator,
    ApplicationProfileReporter applicationProfiles)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task status = ObserveStatusAsync(lifetime.Token);
        Task foreground = ObserveForegroundAsync(lifetime.Token);
        Task profiles = applicationProfiles.RunAsync(lifetime.Token);
        Task completed = await Task.WhenAny(status, foreground, profiles).ConfigureAwait(false);
        Exception? terminalFailure = completed.IsFaulted
            ? completed.Exception?.GetBaseException()
            : null;
        bool endedUnexpectedly = !cancellationToken.IsCancellationRequested
            && terminalFailure is null;
        TryObserveThx(coordinator.MarkUnavailable);
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(status, foreground, profiles).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception) when (terminalFailure is not null || endedUnexpectedly)
        {
        }
        if (terminalFailure is not null)
        {
            ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "Session foreground and application-profile observation stopped unexpectedly.");
    }

    private async Task ObserveStatusAsync(CancellationToken cancellationToken)
    {
        await foreach (ServiceWatchUpdate update in statusWatcher.WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (update is ServiceWatchUpdate.Connected connected)
            {
                TryObserveThx(() => coordinator.Observe(connected.Status));
                applicationProfiles.Observe(connected.Status);
            }
            else
            {
                TryObserveThx(coordinator.MarkUnavailable);
            }
        }
    }

    private async Task ObserveForegroundAsync(CancellationToken cancellationToken)
    {
        await foreach (ThxForegroundObservation observation in foregroundSource.WatchAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            TryObserveThx(() => coordinator.ObserveForeground(observation));
            applicationProfiles.ObserveForeground(observation);
        }
    }

    private static void TryObserveThx(Action observe)
    {
        try
        {
            observe();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "THX application automation observation failed: {0}",
                exception.Message);
        }
    }
}
