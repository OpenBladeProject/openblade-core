using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal static class WriteConfirmationPolicy
{
    // Both entry points share the same early exits so the runtime reads device
    // management state only when the final GPU-reset comparison needs it.
    internal static bool RequiresGpuClockOffsetManagementState(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState confirmed) =>
        MatchWithoutGpuManagementState(subsystem, expectation, confirmed) is null;

    internal static PendingConfirmationResult Match(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState confirmed,
        GpuClockOffsetManagementState? gpuClockOffsetManagementState) =>
        MatchWithoutGpuManagementState(subsystem, expectation, confirmed)
        ?? (gpuClockOffsetManagementState is null
            ? PendingConfirmationResult.Unavailable
            : gpuClockOffsetManagementState is
                GpuClockOffsetManagementState.ResetConfirmed
                or GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                ? PendingConfirmationResult.Match
                : PendingConfirmationResult.Mismatch);

    private static PendingConfirmationResult? MatchWithoutGpuManagementState(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation pending,
        ConfirmedDeviceState confirmed) => subsystem switch
        {
            DeviceSubsystem.Performance => MatchPendingHyperBoost(
                pending,
                confirmed) ?? MatchPendingPerformance(
                    pending,
                    confirmed),
            DeviceSubsystem.Fans => MatchPendingHyperBoost(
                pending,
                confirmed) ?? (confirmed.Fans is null
                    ? PendingConfirmationResult.Unavailable
                    : confirmed.Fans == (pending.Target.FanControl?.Mode
                        ?? pending.Target.Fans.Value)
                        ? PendingConfirmationResult.Match
                        : PendingConfirmationResult.Mismatch),
            DeviceSubsystem.Battery =>
                // Temporary full charge changes the meaning of the other battery
                // fields. A null value is insufficient evidence, so keep the write
                // pending rather than guessing or retrying it.
                confirmed.Battery is not { TemporaryFullCharge: not null } battery
                ? PendingConfirmationResult.Unavailable
                : BatteryMatchesDesired(battery, pending.Target.Battery)
                    && battery.TemporaryFullCharge == pending.Target.Battery.TemporaryFullCharge
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch,
            DeviceSubsystem.Lighting => confirmed.Lighting is null
                ? PendingConfirmationResult.Unavailable
                : confirmed.Lighting == pending.Target.Lighting.Value
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch,
            _ => PendingConfirmationResult.Unavailable,
        };

    private static PendingConfirmationResult? MatchPendingHyperBoost(
        WriteConfirmationExpectation pending,
        ConfirmedDeviceState confirmed)
    {
        if (pending.HyperBoostActive is not bool expected)
        {
            return null;
        }

        if (confirmed.HyperBoostActive != expected)
        {
            return PendingConfirmationResult.Mismatch;
        }

        return expected
            ? confirmed.Fans == FanMode.Automatic
                ? PendingConfirmationResult.Match
                : confirmed.Fans is not null
                    ? PendingConfirmationResult.Mismatch
                    : PendingConfirmationResult.Unavailable
            : confirmed is
            {
                Performance: var restoredPerformance,
                Fans: FanMode.Automatic,
            } && restoredPerformance == pending.HyperBoostBaseMode
                ? PendingConfirmationResult.Match
                : confirmed.Performance is not null
                    && confirmed.Fans is not null
                    ? PendingConfirmationResult.Mismatch
                    : PendingConfirmationResult.Unavailable;
    }

    private static PendingConfirmationResult? MatchPendingPerformance(
        WriteConfirmationExpectation pending,
        ConfirmedDeviceState confirmed)
    {
        FanMode desiredFans = pending.Target.FanControl?.Mode
            ?? pending.Target.Fans.Value;
        if (DeviceSubsystemOrder.IsManualFanMode(desiredFans)
            && DeviceSubsystemOrder.IsManualFanMode(confirmed.Fans))
        {
            // Manual thermal modes do not expose a separate performance preset.
            // This is the same semantic match used by ControlCoordinator.
            return PendingConfirmationResult.Match;
        }

        if (confirmed.Performance is null)
        {
            return PendingConfirmationResult.Unavailable;
        }
        if (confirmed.Performance != pending.Target.Performance.Value
            || confirmed.Performance == PerformancePreset.Custom
                && confirmed.CustomPerformance != pending.Target.CustomPerformance)
        {
            return PendingConfirmationResult.Mismatch;
        }

        if (pending.Target.GpuClockTuning is { } tuning)
        {
            if (!tuning.Enabled)
            {
                if (confirmed.GpuClockOffsets is not null)
                {
                    return PendingConfirmationResult.Mismatch;
                }

                // The runtime obtains management evidence only after these gates.
                return null;
            }

            return confirmed.GpuClockOffsets is null
                ? PendingConfirmationResult.Unavailable
                : confirmed.GpuClockOffsets.Current == tuning.EffectiveOffsets
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch;
        }

        GpuClockOffsetCapabilities? before = pending.PreWriteState.GpuClockOffsets;
        GpuClockOffsetCapabilities? after = confirmed.GpuClockOffsets;
        if (before is null)
        {
            return after is null
                ? PendingConfirmationResult.Match
                : PendingConfirmationResult.Mismatch;
        }
        return after is null
            ? PendingConfirmationResult.Unavailable
            : before.Current == after.Current
                ? PendingConfirmationResult.Match
                : PendingConfirmationResult.Mismatch;
    }

    internal static bool BatteryMatchesDesired(
        ConfirmedBatteryState confirmed,
        DesiredBatteryState desired) =>
        confirmed.ProtectionEnabled == desired.ProtectionEnabled
        && (!desired.ProtectionEnabled
            || confirmed.ChargeLimitPercent == desired.ConfiguredPolicy.ChargeLimitPercent);
}

internal enum PendingConfirmationResult
{
    Unavailable,
    Match,
    Mismatch,
}

internal readonly record struct WriteConfirmationExpectation(
    DesiredDeviceState Target,
    ConfirmedDeviceState PreWriteState,
    bool? HyperBoostActive = null,
    PerformancePreset? HyperBoostBaseMode = null);
