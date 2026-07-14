namespace OpenSynapse.Service;

public sealed class ServiceLifecycleRefreshLoop(
    IServiceControlRuntime runtime,
    IServiceLifecycleEventSource eventSource,
    ILogger logger)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ServiceLifecycleTrigger trigger in
                eventSource.WatchAsync(stoppingToken))
            {
                try
                {
                    await runtime.RefreshAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Service state refresh failed after lifecycle trigger {Trigger}.",
                        trigger);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Windows lifecycle notifications failed.");
        }
    }
}
