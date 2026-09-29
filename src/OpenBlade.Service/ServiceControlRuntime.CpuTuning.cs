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

            CpuTuningStatus observation = await cpuTuningController.ObserveAsync(useInstalledBackend, token)
                .ConfigureAwait(false);
            if (!stillControlsSession() || lowPowerGate.IsActive)
            {
                return new(ControlOperationStatus.Blocked, "The CPU observation session changed.");
            }
            PublishStatusUpdate(status => status with { CpuTuning = observation });
            return new(ControlOperationStatus.Stored,
                "Saved CPU configuration was inspected. Live CPU settings remain unconfirmed.")
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
            if (target != CpuTuningTarget.FirmwareNextBoot || !cpuTuningController.HasSavedBackend
                || !CpuMutationSafetyAvailable(current, stillControlsSession)
                || !CpuFeaturesAdmitted(current.Capabilities, settings, restoring))
            {
                return new(ControlOperationStatus.Blocked,
                    "CPU tuning requires admitted commands, exact restoration, and the active session on full power.")
                { CpuTuning = current.CpuTuning };
            }
            bool FreshAdmission(DeviceIdentity identity, CpuSavedTuningCommand command)
            {
                if (!CpuSavedTuningPlan.IsExactIdentity(identity)
                    || !CpuMutationSafetyAvailable(GetStatus(), stillControlsSession)
                    || !CpuCommandFeatureAdmitted(GetStatus().Capabilities, command)) { return false; }
                ControllerPresenceObservation presence = controllerPresenceMonitor.Observe();
                return presence.DetectionWarning is null && presence.PotentialControllers.Count == 0
                    && powerStateProvider.CapturePowerSource() == PowerSource.PluggedIn
                    && deviceProvider.TryGetVerifiedDevice(out _)
                    && CpuMutationSafetyAvailable(GetStatus(), stillControlsSession);
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

    private static bool CpuCommandFeatureAdmitted(DeviceCapabilities? capabilities, CpuSavedTuningCommand command) =>
        command.Kind == CpuSavedTuningCommandKind.CurveOptimizer
            ? capabilities?.CpuVoltageOptimizer is { Access: DeviceFeatureAccess.ReadWrite }
            : capabilities?.CpuPowerLimit is { Access: DeviceFeatureAccess.ReadWrite };
}
