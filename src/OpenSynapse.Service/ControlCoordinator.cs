using OpenSynapse.Contracts;
using OpenSynapse.Protocol;

namespace OpenSynapse.Service;

public interface IConflictDetector
{
    IReadOnlyList<string> GetRunningConflicts();
}

public sealed class ControlCoordinator(
    IBladeDevice device,
    IConflictDetector conflictDetector)
{
    public async Task<SubsystemApplyResults> ApplyDesiredStateAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        IReadOnlyList<string> conflicts = conflictDetector.GetRunningConflicts();
        if (conflicts.Count != 0)
        {
            ControlResult blocked = new(
                ControlResultStatus.Blocked,
                $"Hardware writes are blocked while these controllers are running: {string.Join(", ", conflicts)}");
            return new SubsystemApplyResults(blocked, blocked, blocked, blocked);
        }

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
        ConfirmedDeviceState confirmed = await device.ReadStateAsync(cancellationToken);

        ControlResult performance = await ApplySubsystemAsync(
            "Performance",
            capabilities.PerformancePresets.Contains(desired.Performance.Value),
            confirmed.Performance == desired.Performance.Value,
            token => device.SetPerformanceAsync(desired.Performance.Value, token),
            cancellationToken);
        ControlResult fans = await ApplySubsystemAsync(
            "Fans",
            capabilities.FanModes.Contains(desired.Fans.Value),
            confirmed.Fans == desired.Fans.Value,
            token => device.SetFanModeAsync(desired.Fans.Value, token),
            cancellationToken);
        bool batterySupported = desired.Battery.ChargeLimitPercent >= capabilities.MinimumChargeLimit
            && desired.Battery.ChargeLimitPercent <= capabilities.MaximumChargeLimit
            && (desired.Battery.ChargeLimitPercent - capabilities.MinimumChargeLimit) % capabilities.ChargeLimitStep == 0;
        ControlResult battery = await ApplySubsystemAsync(
            "Battery",
            batterySupported,
            confirmed.Battery == desired.Battery,
            token => device.SetBatteryPolicyAsync(desired.Battery, token),
            cancellationToken);
        ControlResult lighting = await ApplySubsystemAsync(
            "Lighting",
            capabilities.LightingEffects.Contains(desired.Lighting.Value.Effect),
            confirmed.Lighting == desired.Lighting.Value,
            token => device.SetLightingAsync(desired.Lighting.Value, token),
            cancellationToken);

        return new SubsystemApplyResults(performance, fans, battery, lighting);
    }

    public async Task<ControlResult> ApplyPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> conflicts = conflictDetector.GetRunningConflicts();
        if (conflicts.Count != 0)
        {
            return new ControlResult(
                ControlResultStatus.Blocked,
                $"Hardware writes are blocked while these controllers are running: {string.Join(", ", conflicts)}");
        }

        ConfirmedDeviceState state = await device.ReadStateAsync(cancellationToken);
        if (state.Performance == value)
        {
            return new ControlResult(ControlResultStatus.Unchanged, "Performance is already confirmed.");
        }

        await device.SetPerformanceAsync(value, cancellationToken);
        return new ControlResult(ControlResultStatus.Applied, "Performance setting applied.");
    }

    private static async Task<ControlResult> ApplySubsystemAsync(
        string name,
        bool supported,
        bool unchanged,
        Func<CancellationToken, Task> setter,
        CancellationToken cancellationToken)
    {
        if (!supported)
        {
            return new ControlResult(ControlResultStatus.Blocked, $"{name} value is not supported by the verified capability table.");
        }

        if (unchanged)
        {
            return new ControlResult(ControlResultStatus.Unchanged, $"{name} is already confirmed.");
        }

        try
        {
            await setter(cancellationToken);
            return new ControlResult(ControlResultStatus.Applied, $"{name} setting applied.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ControlResult(ControlResultStatus.Failed, $"{name} failed: {exception.Message}");
        }
    }
}
