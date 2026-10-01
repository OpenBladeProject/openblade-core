using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    public async Task<ControlOperationResult> ObserveCpuTuningAsync(bool useInstalledBackend,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceStatusSnapshot current = GetStatus();
            if (!stillControlsSession() || lowPowerGate.IsActive || !current.DeviceVerified
                || current.Capabilities?.CpuPowerLimit is null
                || !deviceProvider.TryGetRecentlyVerifiedReadOnlyDevice(out _))
            {
                return new(ControlOperationStatus.Blocked,
                    "CPU observation requires the active session and verified supported laptop.");
            }

            long liveGeneration = lowPowerGate.Generation;
            CpuTuningStatus observation = await cpuTuningController.ObserveAsync(useInstalledBackend, token)
                .ConfigureAwait(false);
            if (useInstalledBackend && cpuTuningController.HasLiveBackend)
            {
                CpuTuningStatus live = await cpuTuningController.ObserveLiveAsync(
                    feature => CpuLiveRuntimeAdmission(feature, stillControlsSession, liveGeneration), token).ConfigureAwait(false);
                observation = MergeLiveCpuStatus(observation, live);
            }
            if (!stillControlsSession() || lowPowerGate.IsActive
                || useInstalledBackend && cpuTuningController.HasLiveBackend
                    && !CpuLiveRuntimeAdmission(CpuTuningFeatures.None, stillControlsSession, liveGeneration))
            {
                return new(ControlOperationStatus.Blocked, "The CPU observation session changed.");
            }
            PublishStatusUpdate(status => status with { CpuTuning = observation });
            return new(ControlOperationStatus.Stored,
                observation.LiveState is null ? "CPU configuration was inspected. Live measurements remain unavailable."
                    : "Saved configuration and live CPU observations were inspected.")
            {
                CpuTuning = observation,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return new(ControlOperationStatus.Failed,
                "CPU observation is unavailable; the previous observation was retained.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(ControlOperationStatus.Failed,
                "CPU observation timed out; the previous observation was retained.");
        }
        finally { operationGate.Release(); }
    }

    public async Task<ControlOperationResult> SetCpuTuningAsync(CpuTuningTarget target, CpuTuningSettings settings,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(target) || settings is not { IsWellFormed: true, IsConfigured: true })
        {
            return new(ControlOperationStatus.Failed, "CPU tuning intent is invalid.");
        }
        return await MutateCpuTuningAsync(target, settings, restoring: false,
            stillControlsSession, token).ConfigureAwait(false);
    }

    public async Task<ControlOperationResult> RestoreCpuTuningAsync(CpuTuningTarget target,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(target))
        { return new(ControlOperationStatus.Failed, "CPU tuning target is invalid."); }
        return await MutateCpuTuningAsync(target, null, restoring: true,
            stillControlsSession, token).ConfigureAwait(false);
    }

    private async Task<ControlOperationResult> MutateCpuTuningAsync(CpuTuningTarget target,
        CpuTuningSettings? settings, bool restoring, Func<bool> stillControlsSession, CancellationToken token)
    {
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceStatusSnapshot current = GetStatus();
            if (target == CpuTuningTarget.LiveNow)
            {
                if (!cpuTuningController.HasLiveBackend || !CpuMutationSafetyAvailable(current, stillControlsSession)
                    || !CpuFeaturesAdmitted(current.Capabilities, settings, restoring))
                {
                    return new(ControlOperationStatus.Blocked, "Live CPU tuning requires an admitted backend and fresh full-power session.")
                    { CpuTuning = current.CpuTuning };
                }
                long generation = lowPowerGate.Generation;
                ControlOperationResult live = await cpuTuningController.MutateLiveAsync(settings, restoring,
                    feature => CpuLiveRuntimeAdmission(feature, stillControlsSession, generation), token).ConfigureAwait(false);
                if (live.CpuTuning is { } liveStatus)
                {
                    var merged = MergeLiveCpuStatus(GetStatus().CpuTuning, liveStatus);
                    PublishStatusUpdate(status => status with { CpuTuning = merged });
                    live = live with { CpuTuning = merged };
                }
                return live;
            }
            if (!cpuTuningController.HasSavedBackend
                || !CpuMutationSafetyAvailable(current, stillControlsSession)
                || !CpuSavedFeaturesEligible(current.Capabilities, settings, restoring))
            {
                return new(ControlOperationStatus.Blocked,
                    "CPU tuning requires admitted commands, exact restoration, and the active session on full power.")
                { CpuTuning = current.CpuTuning };
            }
            long savedGeneration = lowPowerGate.Generation;
            bool FreshAdmission(DeviceIdentity identity, CpuSavedTuningCommand command)
            {
                if (lowPowerGate.Generation != savedGeneration || !CpuSavedTuningPlan.IsExactIdentity(identity)
                    || !CpuMutationSafetyAvailable(GetStatus(), stillControlsSession)
                    || !CpuSavedFeaturesEligible(GetStatus().Capabilities, settings, restoring)
                    || !CpuSavedCommandFeatureEligible(GetStatus().Capabilities, command)) { return false; }
                ControllerPresenceObservation presence = controllerPresenceMonitor.Observe();
                return presence.DetectionWarning is null && presence.PotentialControllers.Count == 0
                    && powerStateProvider.CapturePowerSource() == PowerSource.PluggedIn
                    && deviceProvider.TryGetVerifiedDevice(out _)
                    && lowPowerGate.Generation == savedGeneration
                    && CpuMutationSafetyAvailable(GetStatus(), stillControlsSession)
                    && CpuSavedFeaturesEligible(GetStatus().Capabilities, settings, restoring)
                    && CpuSavedCommandFeatureEligible(GetStatus().Capabilities, command);
            }
            ControlOperationResult result = restoring
                ? await cpuTuningController.RestoreSavedAsync(FreshAdmission, token).ConfigureAwait(false)
                : await cpuTuningController.ApplySavedAsync(settings!, FreshAdmission, token).ConfigureAwait(false);
            if (result.CpuTuning is { } observed)
            {
                CpuTuningStatus? previous = GetStatus().CpuTuning;
                observed = observed with
                {
                    SavedConfiguration = observed.SavedConfiguration ?? previous?.SavedConfiguration,
                    LiveState = previous?.LiveState,
                    Live = previous?.Live ?? observed.Live,
                    LiveRecovery = previous?.LiveRecovery,
                    RestorePending = observed.RestorePending || HasCpuRecovery(previous?.LiveRecovery),
                };
                PublishStatusUpdate(status => status with { CpuTuning = observed });
                result = result with { CpuTuning = observed };
            }
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(ControlOperationStatus.Failed, "CPU readback timed out. No further CPU command was submitted.")
            { CpuTuning = GetStatus().CpuTuning };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return new(ControlOperationStatus.Failed, "CPU tuning is unavailable; recovery data was retained.")
            { CpuTuning = GetStatus().CpuTuning };
        }
        finally { operationGate.Release(); }
    }

    private bool CpuLiveRuntimeAdmission(CpuTuningFeatures features, Func<bool> stillControlsSession, long generation)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (lowPowerGate.Generation != generation || !CpuMutationSafetyAvailable(current, stillControlsSession)
            || (features & CpuTuningFeatures.CurveOptimizer) != 0
                && current.Capabilities?.CpuVoltageOptimizer?.Access != DeviceFeatureAccess.ReadWrite
            || (features & CpuTuningFeatures.PowerLimit) != 0
                && current.Capabilities?.CpuPowerLimit?.Access != DeviceFeatureAccess.ReadWrite) { return false; }
        ControllerPresenceObservation presence = controllerPresenceMonitor.Observe();
        return presence.DetectionWarning is null && presence.PotentialControllers.Count == 0
            && powerStateProvider.CapturePowerSource() == PowerSource.PluggedIn
            && deviceProvider.TryGetVerifiedDevice(out _)
            && lowPowerGate.Generation == generation && CpuMutationSafetyAvailable(GetStatus(), stillControlsSession);
    }

    private static CpuTuningStatus MergeLiveCpuStatus(CpuTuningStatus? previous, CpuTuningStatus live) => live with
    {
        SavedConfiguration = previous?.SavedConfiguration,
        Firmware = previous?.Firmware ?? live.Firmware,
        FirmwareRecovery = previous?.FirmwareRecovery,
        PendingBoot = previous?.PendingBoot ?? CpuTuningPendingBoot.Unknown,
        RestorePending = live.RestorePending || HasCpuRecovery(previous?.FirmwareRecovery),
    };
    private static bool HasCpuRecovery(CpuTuningRecoveryStatus? recovery) =>
        recovery is { State: CpuTuningRecoveryState.RestoreReady or CpuTuningRecoveryState.Blocked, Features: not CpuTuningFeatures.None };

    private bool CpuMutationSafetyAvailable(ServiceStatusSnapshot current, Func<bool> stillControlsSession) =>
        stillControlsSession() && !lowPowerGate.IsActive && !thermalConvergenceShutdown
        && current.DeviceVerified && !current.WritesBlocked
        && current.ControlAccess == SessionControlAccess.Active
        && current.ActivePowerSource == PowerSource.PluggedIn
        && current.ControllerDetectionWarning is null && current.PotentialControllers.Count == 0
        && current.UnconfirmedWrites.Count == 0 && !pendingWriteConfirmations.HasPending
        && current.ConfirmedState?.HyperBoostActive != true && !current.HyperBoostRestorePending
        && !hyperBoostJournalSafetyBlock && !hyperBoostRestorePending;

    // The durable reverse plan determines restoration's required features.
    // Every reverse command is admitted before any submission and again at send
    // time; unrelated advertised features confer no authority.
    private static bool CpuFeaturesAdmitted(DeviceCapabilities? capabilities, CpuTuningSettings? settings, bool restoring) =>
        capabilities is not null
        && (restoring || settings is not null
                && (settings.PowerLimitWatts is null || capabilities.CpuPowerLimit is { Access: DeviceFeatureAccess.ReadWrite })
                && (settings.CurveOptimizer is null || capabilities.CpuVoltageOptimizer is { Access: DeviceFeatureAccess.ReadWrite }));

    // Discovery describes the saved feature; the backend independently admits
    // every exact forward and reverse command. It remains production-disabled.
    private static bool CpuSavedFeatureAccessEligible(DeviceFeatureAccess access) =>
        access is DeviceFeatureAccess.ObservedOnly or DeviceFeatureAccess.ReadWrite;

    private static bool CpuSavedPowerEligible(DeviceCapabilities? capabilities) =>
        capabilities?.CpuPowerLimit is { IsWellFormed: true } power && CpuSavedFeatureAccessEligible(power.Access);

    private static bool CpuSavedCurveEligible(DeviceCapabilities? capabilities) =>
        capabilities?.CpuVoltageOptimizer is { IsWellFormed: true } curve && CpuSavedFeatureAccessEligible(curve.Access);

    private static bool CpuSavedFeaturesEligible(DeviceCapabilities? capabilities, CpuTuningSettings? settings, bool restoring) =>
        capabilities is not null && (restoring || settings is { IsWellFormed: true, IsConfigured: true }
            && (settings.PowerLimitWatts is null || CpuSavedPowerEligible(capabilities)
                && capabilities.CpuPowerLimit!.Watts.Supports(settings.PowerLimitWatts.Value))
            && (settings.CurveOptimizer is null || CpuSavedCurveEligible(capabilities)
                && (settings.CurveOptimizer.Enabled
                    ? capabilities.CpuVoltageOptimizer!.Value.Supports(settings.CurveOptimizer.Value)
                    : capabilities.CpuVoltageOptimizer!.SupportsEnableDisable)));

    // Originals are admitted by the exact restoration plan, not the forward
    // editor range. Zero CO still requires explicit disable support; PPT reset
    // is a distinct typed reverse command rather than an editable zero watts.
    private static bool CpuSavedCommandFeatureEligible(DeviceCapabilities? capabilities, CpuSavedTuningCommand command) =>
        command.IsWellFormed && (command.Kind switch
        {
            CpuSavedTuningCommandKind.CurveOptimizer => CpuSavedCurveEligible(capabilities)
                && (command.Value != 0 || capabilities!.CpuVoltageOptimizer!.SupportsEnableDisable),
            CpuSavedTuningCommandKind.PowerLimit or CpuSavedTuningCommandKind.ResetPowerLimit => CpuSavedPowerEligible(capabilities),
            _ => false,
        });
}
