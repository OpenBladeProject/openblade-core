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
        bool startupCompleted = false;
        var startupCoordinator = new ServiceStartupCoordinator(
            profileBootstrapper,
            overrideStore,
            runMarkerStore,
            runtime,
            powerStateProvider,
            logger);
        return RunWithShutdownCleanupAsync(
            token => RunAfterSuccessfulStartupAsync(
                async innerToken =>
                {
                    startupCompleted = await startupCoordinator
                        .InitializeAsync(innerToken).ConfigureAwait(false);
                    return startupCompleted;
                },
                RunBackgroundAsync,
                token),
            (intentionalStop, token) => PrepareForShutdownAsync(
                startupCompleted,
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
        var listenerErrors = new PipeListenerErrorReporter(logger, TimeProvider.System);
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
            runtime.RunThermalConvergenceAsync,
            token => new ServiceFanCurveLoop(runtime, logger).RunAsync(token),
            .. Enumerable.Range(0, MaximumPipeClients)
                .Select(_ => new Func<CancellationToken, Task>(
                    token => RunPipeListenerAsync(
                        server.ServeOnceAsync, logger, listenerErrors, token, Task.Delay))),
        ];
        await ServiceBackgroundTaskGroup.RunAsync(operations, stoppingToken);
    }

    private async Task PrepareForShutdownAsync(
        bool startupCompleted,
        bool intentionalStop,
        CancellationToken cancellationToken)
    {
        bool? clearRunMarker = await PrepareHardwareAndShouldClearRunMarkerAsync(
            startupCompleted,
            intentionalStop,
            runtime.PrepareForShutdownAsync,
            logger,
            cancellationToken).ConfigureAwait(false);

        if (clearRunMarker is null)
        {
            return;
        }

        if (!clearRunMarker.Value)
        {
            string reason = intentionalStop
                ? "intentional-stop hardware cleanup was not confirmed"
                : "the service stopped after an in-process failure";
            logger.LogWarning(
                "The service run marker was retained because {Reason}; the next start will use the safer recovery path.",
                reason);
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

    internal static async Task<bool?> PrepareHardwareAndShouldClearRunMarkerAsync(
        bool startupCompleted,
        bool intentionalStop,
        Func<CancellationToken, Task<ControlOperationResult>> prepareHardwareAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!startupCompleted)
        {
            logger.LogInformation(
                "Service startup did not complete; hardware cleanup and run-marker deletion were skipped.");
            return null;
        }

        try
        {
            ControlOperationResult result = await prepareHardwareAsync(cancellationToken)
                .ConfigureAwait(false);
            LogShutdownCleanupResult(logger, result);
            return intentionalStop
                && result.Status is
                    ControlOperationStatus.Applied or ControlOperationStatus.Stored;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Hardware cleanup timed out during service stop.");
            return false;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Hardware cleanup failed during service stop.");
            return false;
        }
    }

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

    internal static async Task RunPipeListenerAsync(
        Func<CancellationToken, Task> serveOnceAsync,
        ILogger logger,
        PipeListenerErrorReporter listenerErrors,
        CancellationToken stoppingToken,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        // A synchronously failing listener must not trap task-group construction
        // before its siblings and failure supervision have started.
        await Task.Yield();
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(100);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await serveOnceAsync(stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(100);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (PipeListenerEstablishmentException exception)
            {
                listenerErrors.Report(exception, retryDelay);
                await delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 2, 2000));
            }
            catch (Exception exception) when (exception is
                IOException or TimeoutException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Named-pipe connection failed.");
            }
        }
    }

    internal sealed class PipeListenerErrorReporter(ILogger logger, TimeProvider timeProvider)
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);
        private readonly object sync = new();
        private long? lastReportTimestamp;
        private long suppressedFailures;

        internal void Report(PipeListenerEstablishmentException exception, TimeSpan retryDelay)
        {
            long suppressed;
            lock (sync)
            {
                long now = timeProvider.GetTimestamp();
                if (lastReportTimestamp is long last
                    && timeProvider.GetElapsedTime(last, now) < ReportInterval)
                {
                    suppressedFailures++;
                    return;
                }

                lastReportTimestamp = now;
                suppressed = suppressedFailures;
                suppressedFailures = 0;
            }

            // All listeners share this reporting window; their independent,
            // cancellable retries and successful client handling remain unblocked.
            logger.LogError(exception,
                "Named-pipe listener establishment failed; retrying in {RetryDelay}. "
                    + "Suppressed {SuppressedFailures} additional listener failures since the previous report.",
                retryDelay,
                suppressed);
        }
    }

}
