using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ServiceStartupCoordinator(
    MachineProfileBootstrapper profileBootstrapper,
    IServiceControlRuntime runtime,
    ILogger logger)
{
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        ProfileLoadResult profiles = await profileBootstrapper.InitializeAsync(cancellationToken);
        if (profiles.RecoveredFromInvalidFile)
        {
            logger.LogWarning("Invalid machine profiles were replaced with safe defaults.");
        }

        ControlOperationResult profileResult = await runtime.UpdateProfilesAsync(
            profiles.Profiles,
            cancellationToken);
        if (profileResult.Status is not ControlOperationStatus.Stored
            and not ControlOperationStatus.Applied)
        {
            logger.LogCritical(
                "Machine profiles could not be stored during startup: {Status}. {Message}",
                profileResult.Status,
                profileResult.Message);
            return false;
        }

        try
        {
            await runtime.ObserveAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Initial service state observation failed; IPC will expose unavailable state.");
        }

        return true;
    }
}
