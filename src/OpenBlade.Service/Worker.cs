using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class Worker(
    ControlRequestHandler handler,
    IServiceControlRuntime runtime,
    IStatusSubscriptionHub subscriptionHub,
    MachineProfileBootstrapper profileBootstrapper,
    PersistentOverrideStore overrideStore,
    ServiceRunMarkerStore runMarkerStore,
    IPowerStateProvider powerStateProvider,
    IServiceLifecycleEventSource lifecycleEventSource,
    LowPowerGate lowPowerGate,
    IServiceClientContextResolver clientContextResolver,
    ILogger<Worker> logger) : BackgroundService
{
    private const int MaximumPipeClients = NamedPipeControlServer.MaximumServerInstances;
    private static readonly TimeSpan ShutdownCleanupTimeout = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupCoordinator = new ServiceStartupCoordinator(
            profileBootstrapper,
            overrideStore,
            runMarkerStore,
            runtime,
            powerStateProvider,
            logger);
        return RunWithShutdownCleanupAsync(
            token => RunAfterSuccessfulStartupAsync(
                startupCoordinator.InitializeAsync,
                RunBackgroundAsync,
                token),
            (intentionalStop, token) => PrepareForShutdownAsync(
                intentionalStop,
                token),
            stoppingToken);
    }

    internal static async Task RunWithShutdownCleanupAsync(
        Func<CancellationToken, Task> runAsync,
        Func<bool, CancellationToken, Task> cleanupAsync,
        CancellationToken stoppingToken)
    {
        try
        {
            await runAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(
                ShutdownCleanupTimeout);
            await cleanupAsync(
                stoppingToken.IsCancellationRequested,
                cleanupTimeout.Token).ConfigureAwait(false);
        }
    }

    internal static async Task RunAfterSuccessfulStartupAsync(
        Func<CancellationToken, Task<bool>> initializeAsync,
        Func<CancellationToken, Task> runBackgroundAsync,
        CancellationToken stoppingToken)
    {
        if (!await initializeAsync(stoppingToken))
        {
            return;
        }

        await runBackgroundAsync(stoppingToken);
    }

    private async Task RunBackgroundAsync(CancellationToken stoppingToken)
    {
        var server = new NamedPipeControlServer(
            OpenBladeIpc.PipeName,
            handler,
            subscriptionHub,
            clientContextResolver);
        List<Func<CancellationToken, Task>> operations =
        [
            token => new ServiceLifecycleRefreshLoop(
                runtime,
                lifecycleEventSource,
                lowPowerGate,
                logger)
                .RunAsync(token),
            token => new ServicePeriodicObservationLoop(runtime, lowPowerGate, logger)
                .RunAsync(token),
            token => new ServiceFanCurveLoop(runtime, logger).RunAsync(token),
            .. Enumerable.Range(0, MaximumPipeClients)
                .Select(_ => new Func<CancellationToken, Task>(
                    token => RunPipeListenerAsync(server, token))),
        ];
        await ServiceBackgroundTaskGroup.RunAsync(operations, stoppingToken);
    }

    private async Task PrepareForShutdownAsync(
        bool intentionalStop,
        CancellationToken cancellationToken)
    {
        if (!intentionalStop)
        {
            logger.LogWarning(
                "The service stopped unexpectedly; graceful hardware cleanup was skipped and the run marker was retained for bounded recovery on the next SCM start.");
            return;
        }

        bool cleanupCompleted = false;
        try
        {
            ControlOperationResult result = await runtime
                .PrepareForShutdownAsync(cancellationToken)
                .ConfigureAwait(false);
            LogShutdownCleanupResult(logger, result);
            cleanupCompleted = ShouldClearRunMarker(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Hardware cleanup timed out during service stop.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Hardware cleanup failed during service stop.");
        }

        if (!cleanupCompleted)
        {
            logger.LogWarning(
                "The service run marker was retained because intentional-stop hardware cleanup was not confirmed; the next start will use the safer recovery path.");
            return;
        }

        try
        {
            await runMarkerStore.CompleteRunAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "The service run marker was retained because intentional-stop cleanup timed out.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The service run marker could not be cleared after an intentional stop; the next start will use the safer recovery path.");
        }
    }

    internal static bool ShouldClearRunMarker(ControlOperationResult result) =>
        result.Status is ControlOperationStatus.Applied or ControlOperationStatus.Stored;

    internal static void LogShutdownCleanupResult(
        ILogger logger,
        ControlOperationResult result)
    {
        logger.LogInformation(
            "Hardware cleanup during service stop reported {Status}: {Message}",
            result.Status,
            result.Message);

        if (result.Status is not (
            ControlOperationStatus.Applied or ControlOperationStatus.Stored))
        {
            logger.LogWarning(
                "Hardware cleanup did not complete during service stop: {Message}",
                result.Message);
        }
    }

    private async Task RunPipeListenerAsync(
        NamedPipeControlServer server,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await server.ServeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is
                IOException or TimeoutException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Named-pipe connection failed.");
            }
        }
    }

}
