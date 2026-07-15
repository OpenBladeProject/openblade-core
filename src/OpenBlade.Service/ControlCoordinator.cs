using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed class ControlCoordinator(
    IBladeDevice device,
    IHardwareAccessGuard hardwareAccessGuard)
{
    public async Task<SubsystemApplyResults> ApplyDesiredStateAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ControlResult? initialBlock = GetConflictBlock();
        if (initialBlock is not null)
        {
            return All(initialBlock);
        }

        DeviceCapabilities capabilities;
        ConfirmedDeviceState confirmed;
        try
        {
            capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
            confirmed = await device.ReadStateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTerminalDeviceFailure(exception))
        {
            ControlResult failed = new(
                ControlResultStatus.Failed,
                $"Device state could not be read: {exception.Message}");
            return All(failed);
        }

        ApplyOutcome performance = await ApplySubsystemAsync(
            "Performance",
            IsDefined(desired.Performance.Value)
                && capabilities.PerformancePresets.Contains(desired.Performance.Value),
            confirmed.Performance == desired.Performance.Value,
            token => device.SetPerformanceAsync(desired.Performance.Value, token),
            cancellationToken);
        if (performance.StopFurtherWrites)
        {
            return WithBlockedRemainder(performance.Result);
        }

        ApplyOutcome fans = await ApplySubsystemAsync(
            "Fans",
            IsDefined(desired.Fans.Value)
                && capabilities.FanModes.Contains(desired.Fans.Value),
            confirmed.Fans == desired.Fans.Value,
            token => device.SetFanModeAsync(desired.Fans.Value, token),
            cancellationToken);
        if (fans.StopFurtherWrites)
        {
            ControlResult blocked = SubsequentWritesBlocked(fans.Result.Message);
            return new SubsystemApplyResults(performance.Result, fans.Result, blocked, blocked);
        }

        bool batterySupported = capabilities.ChargeLimitStep > 0
            && desired.Battery.ChargeLimitPercent >= capabilities.MinimumChargeLimit
            && desired.Battery.ChargeLimitPercent <= capabilities.MaximumChargeLimit
            && (desired.Battery.ChargeLimitPercent - capabilities.MinimumChargeLimit)
                % capabilities.ChargeLimitStep == 0;
        ApplyOutcome battery = await ApplySubsystemAsync(
            "Battery",
            batterySupported,
            confirmed.Battery == desired.Battery,
            token => device.SetBatteryPolicyAsync(desired.Battery, token),
            cancellationToken);
        if (battery.StopFurtherWrites)
        {
            return new SubsystemApplyResults(
                performance.Result,
                fans.Result,
                battery.Result,
                SubsequentWritesBlocked(battery.Result.Message));
        }

        ApplyOutcome lighting = await ApplySubsystemAsync(
            "Lighting",
            IsDefined(desired.Lighting.Value.Effect)
                && capabilities.LightingEffects.Contains(desired.Lighting.Value.Effect),
            confirmed.Lighting == desired.Lighting.Value,
            token => device.SetLightingAsync(desired.Lighting.Value, token),
            cancellationToken);

        return new SubsystemApplyResults(
            performance.Result,
            fans.Result,
            battery.Result,
            lighting.Result);
    }

    public async Task<ControlResult> ApplyPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken)
    {
        ControlResult? initialBlock = GetConflictBlock();
        if (initialBlock is not null)
        {
            return initialBlock;
        }

        if (!IsDefined(value))
        {
            return Unsupported("Performance");
        }

        try
        {
            DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
            if (!capabilities.PerformancePresets.Contains(value))
            {
                return Unsupported("Performance");
            }

            ConfirmedDeviceState state = await device.ReadStateAsync(cancellationToken);
            if (state.Performance == value)
            {
                return new ControlResult(ControlResultStatus.Unchanged, "Performance is already confirmed.");
            }

            ControlResult? writeBlock = GetConflictBlock();
            if (writeBlock is not null)
            {
                return writeBlock;
            }

            await device.SetPerformanceAsync(value, cancellationToken);
            return new ControlResult(ControlResultStatus.Applied, "Performance setting applied.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ControlResult(
                ControlResultStatus.Failed,
                $"Performance failed: {exception.Message}");
        }
    }

    private async Task<ApplyOutcome> ApplySubsystemAsync(
        string name,
        bool supported,
        bool unchanged,
        Func<CancellationToken, Task> setter,
        CancellationToken cancellationToken)
    {
        if (!supported)
        {
            return new ApplyOutcome(Unsupported(name), StopFurtherWrites: false);
        }

        if (unchanged)
        {
            return new ApplyOutcome(
                new ControlResult(ControlResultStatus.Unchanged, $"{name} is already confirmed."),
                StopFurtherWrites: false);
        }

        ControlResult? conflictBlock = GetConflictBlock();
        if (conflictBlock is not null)
        {
            return new ApplyOutcome(conflictBlock, StopFurtherWrites: true);
        }

        try
        {
            await setter(cancellationToken);
            return new ApplyOutcome(
                new ControlResult(ControlResultStatus.Applied, $"{name} setting applied."),
                StopFurtherWrites: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ApplyOutcome(
                new ControlResult(ControlResultStatus.Failed, $"{name} failed: {exception.Message}"),
                StopFurtherWrites: IsTerminalDeviceFailure(exception));
        }
    }

    private ControlResult? GetConflictBlock()
    {
        HardwareAccessDecision check = hardwareAccessGuard.Check();
        if (check.Safe && check.Conflicts.Count == 0 && check.DetectionFailure is null)
        {
            return null;
        }

        string message = check.DetectionFailure is not null
            ? $"Hardware writes are blocked because controller conflict detection failed: {check.DetectionFailure}"
            : $"Hardware writes are blocked while these controllers are running: {string.Join(", ", check.Conflicts)}";
        return new ControlResult(
                ControlResultStatus.Blocked,
                message);
    }

    private static bool IsTerminalDeviceFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or RazerProtocolException
            or TimeoutException;

    private static bool IsDefined<TEnum>(TEnum value)
        where TEnum : struct, Enum => Enum.IsDefined(value);

    private static ControlResult Unsupported(string name) => new(
        ControlResultStatus.Blocked,
        $"{name} value is not supported by the verified capability table.");

    private static ControlResult SubsequentWritesBlocked(string reason) => new(
        ControlResultStatus.Blocked,
        $"No further hardware writes were attempted: {reason}");

    private static SubsystemApplyResults All(ControlResult result) =>
        new(result, result, result, result);

    private static SubsystemApplyResults WithBlockedRemainder(ControlResult performance)
    {
        ControlResult blocked = SubsequentWritesBlocked(performance.Message);
        return new SubsystemApplyResults(performance, blocked, blocked, blocked);
    }

    private sealed record ApplyOutcome(ControlResult Result, bool StopFurtherWrites);
}
