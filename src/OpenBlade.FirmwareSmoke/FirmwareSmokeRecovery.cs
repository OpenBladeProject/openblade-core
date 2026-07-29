using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

public sealed class FirmwareSmokeRecovery(IFirmwareSmokeClient client)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<FirmwareSmokeCheck>> RestoreAsync(
        FirmwareSmokeSnapshot baseline,
        CancellationToken cancellationToken)
    {
        FirmwareSmokeReport baselineValidation =
            FirmwareSmokeAssessment.ValidateBaseline(baseline);
        if (!baselineValidation.Succeeded)
        {
            throw new InvalidDataException(
                "Baseline recovery requires a snapshot that passed the original read-only health gate.");
        }

        ServiceStatusSnapshot current = await client.ObserveStateAsync(
            LiveReadingSensors.None,
            cancellationToken).ConfigureAwait(false);
        ValidatePreconditions(current, baseline.TargetModel);

        var checks = new List<FirmwareSmokeCheck>
        {
            await RestorePerformanceAsync(baseline, current, cancellationToken)
                .ConfigureAwait(false),
            await RestoreFansAsync(baseline, current, cancellationToken)
                .ConfigureAwait(false),
            await RestoreBatteryAsync(baseline, current, cancellationToken)
                .ConfigureAwait(false),
            await RestoreLightingOverrideAsync(current, cancellationToken)
                .ConfigureAwait(false),
            await RestoreLaptopControlsAsync(baseline, cancellationToken)
                .ConfigureAwait(false),
        };
        return checks;
    }

    private async Task<FirmwareSmokeCheck> RestorePerformanceAsync(
        FirmwareSmokeSnapshot baseline,
        ServiceStatusSnapshot current,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(
            baseline.Controls.Performance,
            ignoreCase: false,
            out PerformancePreset target))
        {
            return Failed(
                "recovery.performance",
                "Restore baseline performance",
                "Baseline performance is unavailable.");
        }

        if (current.ConfirmedState?.Performance == target
            && current.PerformanceOverride is null)
        {
            return Passed(
                "recovery.performance",
                "Restore baseline performance",
                "Already restored.");
        }

        ControlOperationResult applied = await client.SetPerformanceOverrideAsync(
            new PerformanceOverride(target, OverrideLifetime.UntilPowerSourceChange),
            cancellationToken).ConfigureAwait(false);
        if (!IsAccepted(applied)
            || !await WaitForAsync(
                status => status.ConfirmedState?.Performance == target,
                cancellationToken).ConfigureAwait(false))
        {
            return Failed(
                "recovery.performance",
                "Restore baseline performance",
                $"{applied.Status}: {applied.Message}");
        }

        ServiceStatusSnapshot restored = await client.ObserveStateAsync(
            LiveReadingSensors.None,
            cancellationToken).ConfigureAwait(false);
        if (restored.DesiredState.Performance.Value != target)
        {
            return Failed(
                "recovery.performance",
                "Restore baseline performance",
                "Hardware is restored, but the active profile no longer requests the baseline preset; the temporary override was retained.");
        }

        ControlOperationResult cleared = await client.SetPerformanceOverrideAsync(
            value: null,
            cancellationToken).ConfigureAwait(false);
        bool confirmed = await WaitForAsync(
            status => status.ConfirmedState?.Performance == target
                && status.PerformanceOverride is null,
            cancellationToken).ConfigureAwait(false);
        return confirmed
            ? Passed(
                "recovery.performance",
                "Restore baseline performance",
                $"{target}; temporary override cleared.")
            : Failed(
                "recovery.performance",
                "Restore baseline performance",
                $"{cleared.Status}: {cleared.Message}");
    }

    private async Task<FirmwareSmokeCheck> RestoreFansAsync(
        FirmwareSmokeSnapshot baseline,
        ServiceStatusSnapshot current,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(
            baseline.Controls.Fans,
            ignoreCase: false,
            out FanMode target))
        {
            return Failed(
                "recovery.fans",
                "Restore baseline fan mode",
                "Baseline fan mode is unavailable.");
        }

        if (current.ConfirmedState?.Fans == target
            && current.FanOverride is null)
        {
            return Passed(
                "recovery.fans",
                "Restore baseline fan mode",
                "Already restored.");
        }

        if (current.DesiredState.Fans.Value != target)
        {
            return Failed(
                "recovery.fans",
                "Restore baseline fan mode",
                "The active profile no longer requests the baseline fan mode.");
        }

        ControlOperationResult result = await client.SetFanOverrideAsync(
            value: null,
            settings: null,
            cancellationToken).ConfigureAwait(false);
        return IsAccepted(result)
            && await WaitForAsync(
                status => status.ConfirmedState?.Fans == target
                    && status.FanOverride is null,
                cancellationToken).ConfigureAwait(false)
            ? Passed(
                "recovery.fans",
                "Restore baseline fan mode",
                target.ToString())
            : Failed(
                "recovery.fans",
                "Restore baseline fan mode",
                $"{result.Status}: {result.Message}");
    }

    private async Task<FirmwareSmokeCheck> RestoreBatteryAsync(
        FirmwareSmokeSnapshot baseline,
        ServiceStatusSnapshot current,
        CancellationToken cancellationToken)
    {
        if (baseline.Controls.ChargeLimitPercent is not { } target)
        {
            return Failed(
                "recovery.battery",
                "Restore baseline charge limit",
                "Baseline charge limit is unavailable.");
        }

        if (current.ConfirmedState?.Battery?.ChargeLimitPercent == target)
        {
            return Passed(
                "recovery.battery",
                "Restore baseline charge limit",
                "Already restored.");
        }

        ControlOperationResult result = await client.SetBatteryPolicyAsync(
            BatteryPolicy.Create(target),
            cancellationToken).ConfigureAwait(false);
        return IsAccepted(result)
            && await WaitForAsync(
                status => status.ConfirmedState?.Battery?.ChargeLimitPercent == target,
                cancellationToken).ConfigureAwait(false)
            ? Passed(
                "recovery.battery",
                "Restore baseline charge limit",
                $"{target}%")
            : Failed(
                "recovery.battery",
                "Restore baseline charge limit",
                $"{result.Status}: {result.Message}");
    }

    private async Task<FirmwareSmokeCheck> RestoreLightingOverrideAsync(
        ServiceStatusSnapshot current,
        CancellationToken cancellationToken)
    {
        if (current.LightingOverride is null)
        {
            return Passed(
                "recovery.lighting",
                "Clear firmware-smoke lighting override",
                "Already cleared.");
        }

        ControlOperationResult result = await client.SetLightingOverrideAsync(
            value: null,
            cancellationToken).ConfigureAwait(false);
        return IsAccepted(result)
            ? Passed(
                "recovery.lighting",
                "Clear firmware-smoke lighting override",
                result.Message)
            : Failed(
                "recovery.lighting",
                "Clear firmware-smoke lighting override",
                $"{result.Status}: {result.Message}");
    }

    private async Task<FirmwareSmokeCheck> RestoreLaptopControlsAsync(
        FirmwareSmokeSnapshot baseline,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        if (baseline.Controls.StartupAnimationEnabled is { } startup)
        {
            await ApplyIfNeededAsync(
                OneShotLaptopControl.StartupAnimation,
                startup,
                null,
                status => status.StartupAnimationEnabled == startup,
                failures,
                cancellationToken).ConfigureAwait(false);
        }

        if (Enum.TryParse(
            baseline.Controls.FunctionKeyPrimary,
            ignoreCase: false,
            out FunctionKeyPrimaryMode functionMode))
        {
            await ApplyIfNeededAsync(
                OneShotLaptopControl.FunctionKeyPrimary,
                null,
                functionMode,
                status => status.FunctionKeyPrimary == functionMode,
                failures,
                cancellationToken).ConfigureAwait(false);
        }

        if (baseline.Controls.GamingModeEnabled is { } gaming)
        {
            await ApplyIfNeededAsync(
                OneShotLaptopControl.GamingMode,
                gaming,
                null,
                status => status.GamingModeEnabled == gaming,
                failures,
                cancellationToken).ConfigureAwait(false);
        }

        return failures.Count == 0
            ? Passed(
                "recovery.laptop-controls",
                "Restore baseline laptop controls",
                "Startup, function-key primary, and Gaming mode restored.")
            : Failed(
                "recovery.laptop-controls",
                "Restore baseline laptop controls",
                string.Join("; ", failures));
    }

    private async Task ApplyIfNeededAsync(
        OneShotLaptopControl control,
        bool? enabled,
        FunctionKeyPrimaryMode? functionMode,
        Func<ServiceStatusSnapshot, bool> predicate,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot current = await client.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        if (predicate(current))
        {
            return;
        }

        ControlOperationResult result = await client.ApplyLaptopControlAsync(
            new ApplyOneShotLaptopControl(control, enabled, functionMode),
            cancellationToken).ConfigureAwait(false);
        if (!IsAccepted(result)
            || !await WaitForAsync(predicate, cancellationToken).ConfigureAwait(false))
        {
            failures.Add($"{control}: {result.Status}: {result.Message}");
        }
    }

    private async Task<bool> WaitForAsync(
        Func<ServiceStatusSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Timeout;
        do
        {
            ServiceStatusSnapshot status = await client.ObserveStateAsync(
                LiveReadingSensors.None,
                cancellationToken).ConfigureAwait(false);
            if (predicate(status))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    private static void ValidatePreconditions(
        ServiceStatusSnapshot status,
        string baselineTargetModel)
    {
        string? currentModel = RazerBladeModelCatalog.Find(
            status.Firmware?.ModelNumber)?.ModelNumber;
        if (!status.DeviceVerified
            || status.WritesBlocked
            || status.PotentialControllers.Count > 0
            || status.ActivePowerSource != PowerSource.PluggedIn
            || !string.Equals(
                currentModel,
                RazerBladeModelCatalog.Find(baselineTargetModel)?.ModelNumber,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Baseline recovery requires the same verified, write-enabled device on the full-power AC adapter with no controller conflict.");
        }
    }

    private static bool IsAccepted(ControlOperationResult result) =>
        result.Status is ControlOperationStatus.Applied
            or ControlOperationStatus.Stored;

    private static FirmwareSmokeCheck Passed(
        string id,
        string description,
        string actual) =>
        new(
            id,
            description,
            FirmwareSmokeOutcome.Passed,
            "Baseline restored",
            actual);

    private static FirmwareSmokeCheck Failed(
        string id,
        string description,
        string actual) =>
        new(
            id,
            description,
            FirmwareSmokeOutcome.Failed,
            "Baseline restored",
            actual);
}
