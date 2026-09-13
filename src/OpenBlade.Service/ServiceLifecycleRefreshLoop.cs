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

public sealed class ServiceLifecycleRefreshLoop
{
    private readonly IServiceControlRuntime runtime;
    private readonly IServiceLifecycleEventSource eventSource;
    private readonly LowPowerGate lowPowerGate;
    private readonly ILogger logger;

    public ServiceLifecycleRefreshLoop(
        IServiceControlRuntime runtime,
        IServiceLifecycleEventSource eventSource,
        LowPowerGate lowPowerGate,
        ILogger logger)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        this.lowPowerGate = lowPowerGate
            ?? throw new ArgumentNullException(nameof(lowPowerGate));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ServiceLifecycleTrigger trigger in
                eventSource.WatchAsync(stoppingToken))
            {
                try
                {
                    logger.LogInformation(
                        "Processing Windows lifecycle trigger {Trigger}.",
                        trigger);
                    switch (trigger)
                    {
                        case ServiceLifecycleTrigger.PowerChanged:
                            if (lowPowerGate.IsActive)
                            {
                                logger.LogInformation(
                                    "Skipping power-source transition while the low-power gate is active.");
                            }
                            else
                            {
                                await runtime.CancelResumeValidationAsync(stoppingToken);
                                await runtime.HandlePowerSourceChangedAsync(stoppingToken);
                            }
                            break;
                        case ServiceLifecycleTrigger.Suspending:
                            await runtime.PrepareForLowPowerAsync(stoppingToken);
                            await runtime.InvalidateLightingRendererAsync(stoppingToken);
                            break;
                        case ServiceLifecycleTrigger.Resumed:
                            await runtime.InvalidateLightingRendererAsync(stoppingToken);
                            await runtime.BeginResumeValidationAsync(stoppingToken);
                            if (lowPowerGate.IsActive)
                            {
                                await runtime.CancelResumeValidationAsync(stoppingToken);
                                logger.LogInformation(
                                    "Skipping power-source transition and lighting-renderer restoration after resume while the low-power gate remains active.");
                            }
                            else
                            {
                                await runtime.HandlePowerSourceChangedAsync(stoppingToken);
                                await runtime.RestoreLightingRendererAsync(stoppingToken);
                                await runtime.CompleteResumeValidationAsync(stoppingToken);
                            }
                            break;
                        case ServiceLifecycleTrigger.DisplayOff:
                            // The console display state is recorded unconditionally: it
                            // is a fact about the laptop, not a lighting policy decision,
                            // and the transition classifier needs it even when the
                            // active profile keeps the keyboard lit or the gate is active.
                            runtime.ReportConsoleDisplayState(displayOn: false);
                            if (lowPowerGate.IsActive)
                            {
                                logger.LogInformation(
                                    "Skipping display lighting suppression transition while the low-power gate is active.");
                            }
                            else
                            {
                                // Display-off is not proof that Modern Standby sleep began. It is
                                // nevertheless the conservative boundary for ending only an
                                // OpenBlade-owned HyperBoost session, because this exact host did not
                                // deliver a suspend callback before entering Modern Standby. Do not
                                // release unrelated ownership or treat this as full low-power entry.
                                await runtime.PrepareOwnedHyperBoostForDisplayOffAsync(
                                    stoppingToken);
                                await runtime.SetLightingSuppressionAsync(
                                    OpenBlade.Contracts.LightingSuppressionReason.DisplayOff,
                                    suppressed: true,
                                    stoppingToken);
                            }
                            break;
                        case ServiceLifecycleTrigger.DisplayOn:
                            runtime.ReportConsoleDisplayState(displayOn: true);
                            if (lowPowerGate.IsActive)
                            {
                                logger.LogInformation(
                                    "Skipping display lighting suppression transition while the low-power gate is active.");
                            }
                            else
                            {
                                await runtime.SetLightingSuppressionAsync(
                                    OpenBlade.Contracts.LightingSuppressionReason.DisplayOff,
                                    suppressed: false,
                                    stoppingToken);
                            }
                            break;
                        case ServiceLifecycleTrigger.DeviceArrived:
                            if (lowPowerGate.IsActive)
                            {
                                logger.LogInformation(
                                    "Skipping device observation and lighting-renderer restoration after device arrival while the low-power gate is active.");
                            }
                            else
                            {
                                await runtime.ObserveAsync(
                                    includeLiveReadings: false,
                                    stoppingToken);
                                await runtime.RestoreLightingRendererAsync(stoppingToken);
                            }
                            break;
                        case ServiceLifecycleTrigger.DeviceRemoved:
                            await runtime.InvalidateLightingRendererAsync(stoppingToken);
                            if (lowPowerGate.IsActive)
                            {
                                logger.LogInformation(
                                    "Skipping device observation after device removal while the low-power gate is active.");
                            }
                            else
                            {
                                await runtime.ObserveAsync(
                                    includeLiveReadings: false,
                                    stoppingToken);
                            }
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
