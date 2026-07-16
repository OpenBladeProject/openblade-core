namespace OpenBlade.Service;

public static class ServiceBackgroundTaskGroup
{
    public static async Task RunAsync(
        IEnumerable<Func<CancellationToken, Task>> operations,
        CancellationToken stoppingToken)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] tasks = operations
            .Select(operation => operation(linkedCancellation.Token))
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        Task firstCompleted = await Task.WhenAny(tasks);
        Exception? firstFailure = null;
        try
        {
            await firstCompleted;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        await linkedCancellation.CancelAsync();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch when (firstFailure is not null || stoppingToken.IsCancellationRequested)
        {
        }

        if (firstFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(firstFailure)
                .Throw();
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "An OpenBlade service background component stopped unexpectedly.");
        }
    }
}

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
                    switch (trigger)
                    {
                        case ServiceLifecycleTrigger.PowerChanged:
                            await runtime.HandlePowerSourceChangedAsync(stoppingToken);
                            break;
                        case ServiceLifecycleTrigger.Resumed:
                        case ServiceLifecycleTrigger.DeviceArrived:
                        case ServiceLifecycleTrigger.DeviceRemoved:
                            await runtime.ObserveAsync(stoppingToken);
                            break;
                    }
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
            throw;
        }
    }
}
