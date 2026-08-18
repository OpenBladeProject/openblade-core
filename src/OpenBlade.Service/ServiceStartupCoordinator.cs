using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ServiceStartupCoordinator(
    MachineProfileBootstrapper profileBootstrapper,
    PersistentOverrideStore overrideStore,
    ServiceRunMarkerStore runMarkerStore,
    IServiceOwnershipJournalCleaner ownershipJournalCleaner,
    IServiceControlRuntime runtime,
    IPowerStateProvider powerStateProvider,
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

        PersistentOverrideLoadResult persistentOverrides =
            await overrideStore.LoadAsync(cancellationToken);
        if (persistentOverrides.Recovered)
        {
            logger.LogWarning(
                "Invalid persistent overrides were cleared to safe defaults.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ServiceStartupKind startupKind = await runMarkerStore
            .BeginRunAsync(cancellationToken)
            .ConfigureAwait(false);
        bool recoveringUnexpectedExit =
            startupKind == ServiceStartupKind.UnexpectedExitRecovery;
        if (!recoveringUnexpectedExit)
        {
            await ownershipJournalCleaner.ClearAsync(cancellationToken)
                .ConfigureAwait(false);
        }
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
