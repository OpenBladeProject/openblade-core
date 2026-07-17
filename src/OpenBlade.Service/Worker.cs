using System.Security.Principal;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class Worker(
    ControlRequestHandler handler,
    IServiceControlRuntime runtime,
    IStatusSubscriptionHub subscriptionHub,
    MachineProfileBootstrapper profileBootstrapper,
    PersistentOverrideStore overrideStore,
    IPowerStateProvider powerStateProvider,
    IServiceLifecycleEventSource lifecycleEventSource,
    ILogger<Worker> logger) : BackgroundService
{
    private const int MaximumPipeClients = 4;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupCoordinator = new ServiceStartupCoordinator(
            profileBootstrapper,
            overrideStore,
            runtime,
            powerStateProvider,
            logger);
        return RunAfterSuccessfulStartupAsync(
            startupCoordinator.InitializeAsync,
            RunBackgroundAsync,
            stoppingToken);
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
        SecurityIdentifier? authorizedUser = ReadAuthorizedUser();
        if (authorizedUser is null)
        {
            logger.LogCritical("Authorized user SID is missing; the IPC service will not start.");
            return;
        }

        var server = new NamedPipeControlServer(
            OpenBladeIpc.PipeName,
            authorizedUser,
            handler,
            subscriptionHub);
        List<Func<CancellationToken, Task>> operations =
        [
            token => new ServiceLifecycleRefreshLoop(runtime, lifecycleEventSource, logger)
                .RunAsync(token),
            .. Enumerable.Range(0, MaximumPipeClients)
                .Select(_ => new Func<CancellationToken, Task>(
                    token => RunPipeListenerAsync(server, token))),
        ];
        await ServiceBackgroundTaskGroup.RunAsync(operations, stoppingToken);
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Named-pipe connection failed.");
            }
        }
    }

    private static SecurityIdentifier? ReadAuthorizedUser()
    {
        string? configured = Environment.GetEnvironmentVariable("OPENBLADE_USER_SID");
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade",
            "authorized-user.sid");

        string? value = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : File.Exists(path) ? File.ReadAllText(path).Trim() : null;

        return string.IsNullOrWhiteSpace(value) ? null : new SecurityIdentifier(value);
    }
}
