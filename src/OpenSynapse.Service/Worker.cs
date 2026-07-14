using System.Security.Principal;
using OpenSynapse.Contracts;

namespace OpenSynapse.Service;

public sealed class Worker(
    StatusRequestHandler handler,
    MachineProfileBootstrapper profileBootstrapper,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ProfileLoadResult profiles = await profileBootstrapper.InitializeAsync(stoppingToken);
        if (profiles.RecoveredFromInvalidFile)
        {
            logger.LogWarning("Invalid machine profiles were replaced with safe defaults.");
        }

        SecurityIdentifier? authorizedUser = ReadAuthorizedUser();
        if (authorizedUser is null)
        {
            logger.LogCritical("Authorized user SID is missing; the IPC service will not start.");
            return;
        }

        NamedPipeStatusServer server = new(
            OpenSynapseIpc.PipeName,
            authorizedUser,
            handler);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await server.ServeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Named-pipe request failed.");
            }
        }
    }

    private static SecurityIdentifier? ReadAuthorizedUser()
    {
        string? configured = Environment.GetEnvironmentVariable("OPENSYNAPSE_USER_SID");
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenSynapse",
            "authorized-user.sid");

        string? value = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : File.Exists(path) ? File.ReadAllText(path).Trim() : null;

        return string.IsNullOrWhiteSpace(value) ? null : new SecurityIdentifier(value);
    }
}
