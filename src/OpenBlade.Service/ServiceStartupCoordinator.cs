using OpenBlade.Contracts;
using OpenBlade.Core.Native;

namespace OpenBlade.Service;

public sealed class ServiceStartupCoordinator(
    MachineProfileBootstrapper profileBootstrapper,
    PersistentOverrideStore overrideStore,
    ServiceRunMarkerStore runMarkerStore,
    IServiceControlRuntime runtime,
    IPowerStateProvider powerStateProvider,
    ILogger logger)
{
    // Post-initialization startup can already own Driver mode before later
    // reconciliation fails. It still needs the normal guarded shutdown cleanup.
    internal bool RuntimeInitialized { get; private set; }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        StartupConfiguration? startup;
        ServiceStartupKind startupKind;
        if (runMarkerStore.RequiresBackendRetirement)
        {
            MachineConfigurationWriterBinding binding = overrideStore.MachineWriter
                ?? throw new InvalidOperationException("Machine startup requires an enrolled override writer.");
            try
            {
                (startupKind, startup) = await runMarkerStore.BeginTransitionAsync(
                    binding, LoadConfigurationAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfigurationQuarantinedException exception)
            {
                LogRecoveryRequired(exception);
                return false;
            }
        }
        else
        {
            startup = await LoadConfigurationAsync(null, cancellationToken);
            if (startup is null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            startupKind = await runMarkerStore.BeginRunAsync(cancellationToken).ConfigureAwait(false);
        }

        if (startup is null)
        {
            return false;
        }

        bool recoveringManagedRun =
            startupKind == ServiceStartupKind.UnexpectedExitRecovery;
        bool recoveringUnexpectedExit = recoveringManagedRun;
        if (recoveringUnexpectedExit)
        {
            logger.LogWarning(
                "An uncleared service run marker was found. OpenBlade will observe fresh state and recover only owned long-running behavior.");
        }
        else if (runtime.NativeHandbackRecoveryPending)
        {
            // The previous native service crashed with writes managed must
            // confirm. Recover as after an unexpected exit; apply no profile.
            // Without a managed run marker, managed owned no fan state.
            recoveringUnexpectedExit = true;
            logger.LogWarning(
                "The previous native service left unconfirmed changes. OpenBlade will observe fresh state before any write.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        PowerSource powerSource = powerStateProvider.CapturePowerSource();
        cancellationToken.ThrowIfCancellationRequested();
        runtime.Initialize(
            startup.Configuration,
            powerSource,
            startup.Overrides,
            recoveringUnexpectedExit,
            recoveringManagedRun);
        RuntimeInitialized = true;

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
        catch (Exception exception) when (exception is not NativeComponentException)
        {
            logger.LogError(
                exception,
                "Initial service state observation failed; IPC will expose unavailable state.");
        }

        return true;
    }

    private async Task<StartupConfiguration?> LoadConfigurationAsync(
        UserConfigurationWriterScope? scope,
        CancellationToken cancellationToken)
    {
        BatteryConfiguration configuration;
        try
        {
            configuration = scope is null
                ? await profileBootstrapper.InitializeAsync(cancellationToken)
                : await profileBootstrapper.InitializeTransitionAsync(
                    overrideStore.MachineWriter!, scope, cancellationToken);
        }
        catch (ConfigurationQuarantinedException exception)
        {
            // No authoritative bootstrap intent exists. Do not initialize a
            // default-valued runtime: nonthermal paths also need known intent.
            LogRecoveryRequired(exception);
            return null;
        }
        if (configuration.Disposition == ConfigurationLoadDisposition.Quarantined)
        {
            logger.LogWarning(
                "Invalid or unsupported battery configuration was preserved in quarantine; ordinary thermal writes are disabled.");
        }

        PersistentOverrideLoadResult persistentOverrides;
        try
        {
            persistentOverrides = scope is null
                ? await overrideStore.LoadAsync(cancellationToken)
                : await overrideStore.LoadForMachineTransitionAsync(scope, cancellationToken);
        }
        catch (Exception exception) when (overrideStore.MachineWriter is not null &&
            exception is (IOException or UnauthorizedAccessException or ConfigurationQuarantinedException))
        {
            logger.LogError(exception,
                "Machine overrides could not be admitted; service initialization was refused before runtime or background work.");
            return null;
        }
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

        return new StartupConfiguration(configuration, persistentOverrides.Overrides);
    }

    private void LogRecoveryRequired(ConfigurationQuarantinedException exception) =>
        logger.LogError(exception,
            "Machine configuration transaction recovery is required; service initialization was refused before runtime or background work. Preserve the recovery record and original transaction artifacts.");

    private sealed record StartupConfiguration(
        BatteryConfiguration Configuration,
        PersistentOverrideState Overrides);
}
