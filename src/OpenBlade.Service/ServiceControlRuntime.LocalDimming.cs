using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    // Local dimming has a one-time Clear and no saved intent. The exact port
    // re-reads the panel and Power Mode Control before its single write and
    // confirms by readback; a possible submission without confirmation pauses
    // that backend's writes and is never replayed here.
    public async Task<ControlOperationResult> ClearLocalDimmingAsync(CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            ServiceStatusSnapshot current = GetStatus();
            if (!current.DeviceVerified
                || current.WritesBlocked
                || current.Capabilities?.LocalDimming is not { SupportsClear: true }
                || lowPowerGate.IsActive
                || activeUserStillControlsSession?.Invoke() == false
                || !HasFreshDirectControlPowerContext()
                || !deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Local dimming can't be turned off right now.");
            }

            ControlOperationResult result;
            bool? confirmedActive = null;
            try
            {
                await device.ClearLocalDimmingAsync(token).ConfigureAwait(false);
                confirmedActive = false;
                result = new(ControlOperationStatus.Applied, "Local dimming is off.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (CapabilityUnavailableException exception)
            {
                result = new(ControlOperationStatus.Blocked, exception.Message);
            }
            catch (BladeWriteException exception) when (
                exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                result = new(ControlOperationStatus.Blocked, exception.Message);
            }
            catch (Exception)
            {
                result = new(ControlOperationStatus.Failed,
                    "OpenBlade couldn't confirm whether local dimming changed. Laptop control changes are paused until the laptop reports its settings again.");
            }

            history.RecordSummary(result.Status, result.Message);
            ServiceStatusSnapshot latest = GetStatus();
            PublishIfChanged(latest with
            {
                ConfirmedState = latest.ConfirmedState is { } state
                    ? state with { LocalDimmingActive = confirmedActive }
                    : null,
                Message = result.Message,
            });
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }
}
