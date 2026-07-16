using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ServiceStartupCoordinator(
    MachineProfileBootstrapper profileBootstrapper,
    IServiceControlRuntime runtime,
    IPowerTelemetryProvider telemetryProvider,
    ILogger logger)
{
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        BatteryConfiguration configuration =
            await profileBootstrapper.InitializeAsync(cancellationToken);
        if (configuration.Recovered)
        {
            logger.LogWarning(
                "Invalid battery configuration was replaced with safe defaults.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        PowerSource powerSource = telemetryProvider.Capture().PowerSource;
        cancellationToken.ThrowIfCancellationRequested();
        runtime.Initialize(configuration, powerSource);

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
