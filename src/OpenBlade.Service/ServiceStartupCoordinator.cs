using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ServiceStartupCoordinator(
    MachineProfileBootstrapper profileBootstrapper,
    PersistentOverrideStore overrideStore,
    ServiceRunMarkerStore runMarkerStore,
    IServiceControlRuntime runtime,
    IPowerStateProvider powerStateProvider,
    ILogger logger)
{
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        BatteryConfiguration configuration =
            await profileBootstrapper.InitializeAsync(cancellationToken);
        if (configuration.Disposition == ConfigurationLoadDisposition.Quarantined)
        {
            logger.LogWarning(
                "Invalid or unsupported battery configuration was preserved in quarantine; ordinary thermal writes are disabled.");
        }

        PersistentOverrideLoadResult persistentOverrides =
            await overrideStore.LoadAsync(cancellationToken);
        if (persistentOverrides.Disposition == ConfigurationLoadDisposition.Quarantined)
        {
            logger.LogWarning(
                "Invalid or unsupported persistent overrides were preserved in quarantine; ordinary thermal writes are disabled.");
            configuration = configuration with
            {
                Disposition = ConfigurationLoadDisposition.Quarantined,
                QuarantineReason = persistentOverrides.QuarantineReason,
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        ServiceStartupKind startupKind = await runMarkerStore
            .BeginRunAsync(cancellationToken)
            .ConfigureAwait(false);
        bool recoveringUnexpectedExit =
            startupKind == ServiceStartupKind.UnexpectedExitRecovery;
        if (recoveringUnexpectedExit)
        {
            logger.LogWarning(
                "An uncleared service run marker was found. OpenBlade will observe fresh state and recover only owned long-running behavior.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        PowerSource powerSource = powerStateProvider.CapturePowerSource();
        cancellationToken.ThrowIfCancellationRequested();
        runtime.Initialize(
            configuration,
            powerSource,
            persistentOverrides.Overrides,
            recoveringUnexpectedExit);

        try
        {
            await runtime.ObserveAsync(includeLiveReadings: false, cancellationToken);
            await runtime.MaintainDriverModeAsync(cancellationToken);
            if (recoveringUnexpectedExit)
            {
                await runtime.ReconcileUnexpectedExitAsync(cancellationToken);
                await runtime.PrepareManualFanCurveRecoveryAsync(cancellationToken);
                await runtime.MaintainFanCurveAsync(cancellationToken);
            }
            else
            {
                await runtime.ReconcileStartupProfileAsync(cancellationToken);
            }
            await runtime.RestoreLightingRendererAsync(cancellationToken);
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
