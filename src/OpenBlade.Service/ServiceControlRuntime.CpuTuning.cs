using OpenBlade.Contracts;

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
        finally { operationGate.Release(); }
    }

    public Task<ControlOperationResult> SetCpuTuningAsync(CpuTuningTarget target, CpuTuningSettings settings,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(target) || settings is not { IsWellFormed: true, IsConfigured: true })
        {
            return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Failed, "CPU tuning intent is invalid."));
        }
        // Neither backend has attended write/readback/restoration admission.
        // Preference, file presence, saved values and backend availability can
        // never promote admission or begin an ownership journal.
        return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked,
            stillControlsSession()
                ? "CPU tuning requires attended write, readback and restoration validation."
                : "Another session currently controls the laptop.")
        { CpuTuning = GetStatus().CpuTuning });
    }

    public Task<ControlOperationResult> RestoreCpuTuningAsync(CpuTuningTarget target,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new ControlOperationResult(
            Enum.IsDefined(target) ? ControlOperationStatus.Blocked : ControlOperationStatus.Failed,
            "No admitted CPU write or OpenBlade-owned CPU restoration baseline exists.")
        {
            CpuTuning = GetStatus().CpuTuning,
        });
    }
}
