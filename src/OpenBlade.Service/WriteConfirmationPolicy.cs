using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal static class WriteConfirmationPolicy
{
    // Both entry points share the same early exits so the runtime reads device
    // management state only when the final GPU comparison needs it.
    internal static bool RequiresGpuClockOffsetManagementState(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState confirmed) =>
        MatchWithoutGpuManagementState(subsystem, expectation, confirmed) is null;

    // A null management state means the caller supplied no GPU knowledge, so the
    // GPU half stays Unavailable. An uncertain NVML submission also stays
    // Unavailable until a driver read resolves it into real offsets. With the
    // device's management state, a disabled target needs explicit reset evidence.
    // Any other GPU half without a managed offset observation is
    // GpuClockOffsetsUnconfirmed: every non-GPU field has already matched, NVML
    // either never ran or left no uncertain submission, and the caller retires
    // the GPU half instead of waiting for an NVIDIA reading that may never come
    // (an idle dGPU, a lost confirmation or a paired write whose NVML step never
    // ran). It never counts as a match. Manual fan modes hide the preset, but
    // their GPU half is judged the same way.
    internal static PendingConfirmationResult Match(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState confirmed,
        GpuClockOffsetManagementState? gpuClockOffsetManagementState) =>
        MatchWithoutGpuManagementState(subsystem, expectation, confirmed)
        ?? (gpuClockOffsetManagementState is null
                or GpuClockOffsetManagementState.SubmissionUncertain
            ? PendingConfirmationResult.Unavailable
            : expectation.Target.GpuClockTuning is not { Enabled: false }
                ? PendingConfirmationResult.GpuClockOffsetsUnconfirmed
            : gpuClockOffsetManagementState is
                GpuClockOffsetManagementState.ResetConfirmed
                or GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                ? PendingConfirmationResult.Match
                : PendingConfirmationResult.Mismatch);

    // A crashed native write is confirmed against its own target only. Native
    // performance never runs under owned manual fans, so manual fans are a
    // mismatch; native fan targets compare by Automatic versus manual topology.
    // Native sets the two thermal channels in turn, so a crash between them can
    // leave a pair that reads as no state at all. A pair that still disagrees
    // after the read's own retry is a definite firmware state other than the
    // target. It survives restarts, so it is reported for an explicit reapply,
    // as managed's own interrupted write is after a restart, never waited on.
    // A Performance target read while HyperBoost is active reports no preset,
    // but it is a definite state too: a native HyperBoost exit stopped before
    // its first setter leaves HyperBoost on, which no later read changes.
    internal static PendingConfirmationResult MatchNativeHandback(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState confirmed,
        NativeHandbackClaim claim,
        bool thermalChannelsDisagree = false)
    {
        if (!claim.Resolvable)
        {
            return PendingConfirmationResult.Unavailable;
        }
        return subsystem switch
        {
            // Native owned HyperBoost's enable pair: HyperBoost with Automatic
            // fans, as managed confirms its own entry. A pair that still
            // disagrees is a definite state other than the target.
            DeviceSubsystem.Performance when expectation.HyperBoostActive is true =>
                MatchPendingHyperBoost(expectation, confirmed) switch
                {
                    PendingConfirmationResult.Unavailable when thermalChannelsDisagree => PendingConfirmationResult.Mismatch,
                    { } result => result,
                    null => PendingConfirmationResult.Unavailable,
                },
            DeviceSubsystem.Performance => DeviceSubsystemOrder.IsManualFanMode(confirmed.Fans)
                ? PendingConfirmationResult.Mismatch
                : confirmed.Performance is not { } preset
                    ? thermalChannelsDisagree || confirmed.HyperBoostActive is true
                        ? PendingConfirmationResult.Mismatch
                        : PendingConfirmationResult.Unavailable
                    : preset == expectation.Target.Performance.Value
                        ? PendingConfirmationResult.Match
                        : PendingConfirmationResult.Mismatch,
            DeviceSubsystem.Fans => confirmed.Fans is not { } fans
                ? thermalChannelsDisagree ? PendingConfirmationResult.Mismatch : PendingConfirmationResult.Unavailable
                : DeviceSubsystemOrder.IsManualFanMode(fans)
                    == DeviceSubsystemOrder.IsManualFanMode(expectation.Target.Fans.Value)
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch,
            DeviceSubsystem.Battery => MatchPendingBattery(expectation, confirmed.Battery),
            _ => PendingConfirmationResult.Unavailable,
        };
    }

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
            DeviceSubsystem.Battery => MatchPendingBattery(pending, confirmed.Battery),
            DeviceSubsystem.Lighting => MatchPendingLighting(pending.Target.Lighting.Value, confirmed),
            _ => PendingConfirmationResult.Unavailable,
        };

    private static PendingConfirmationResult MatchPendingLighting(
        LightingSettings requested,
        ConfirmedDeviceState confirmed)
    {
        PendingConfirmationResult independent = requested.IndependentControls is { } fields
            ? MatchIndependentLighting(fields, confirmed)
            : PendingConfirmationResult.Match;
        if (!requested.ControlsKeyboardEffect && requested.IndependentControls is not null)
        {
            return independent;
        }
        LogoLightingMode? observedLogo = confirmed.LogoLighting
            ?? confirmed.IndependentLighting?.LogoMode
            ?? confirmed.Lighting?.LogoMode;
        if (confirmed.Lighting is null
            || requested.LogoMode.HasValue && requested.IndependentControls?.LogoMode is null
                && !observedLogo.HasValue)
        {
            return PendingConfirmationResult.Unavailable;
        }

        bool keyboardMatches = ControlCoordinator.KeyboardLightingMatches(
            confirmed with { LogoLighting = observedLogo }, requested);
        return keyboardMatches ? independent : PendingConfirmationResult.Mismatch;
    }

    private static PendingConfirmationResult MatchIndependentLighting(
        IndependentLightingSettings requested, ConfirmedDeviceState confirmed)
    {
        byte? brightness = confirmed.IndependentLighting?.Brightness;
        LogoLightingMode? logo = confirmed.IndependentLighting?.LogoMode ?? confirmed.LogoLighting;
        if (requested.Brightness.HasValue && !brightness.HasValue || requested.LogoMode.HasValue && !logo.HasValue
            || requested.VaporChamberLightingEnabled.HasValue && confirmed.IndependentLighting?.VaporChamberLightingEnabled is null
            || requested.TouchpadStatusIndicatorEnabled.HasValue && confirmed.IndependentLighting?.TouchpadStatusIndicatorEnabled is null)
        {
            return PendingConfirmationResult.Unavailable;
        }
        return (!requested.Brightness.HasValue || requested.Brightness == brightness)
            && (!requested.VaporChamberLightingEnabled.HasValue || requested.VaporChamberLightingEnabled == confirmed.IndependentLighting?.VaporChamberLightingEnabled)
            && (!requested.TouchpadStatusIndicatorEnabled.HasValue || requested.TouchpadStatusIndicatorEnabled == confirmed.IndependentLighting?.TouchpadStatusIndicatorEnabled)
            && (!requested.LogoMode.HasValue || requested.LogoMode == logo)
                ? PendingConfirmationResult.Match : PendingConfirmationResult.Mismatch;
    }

    private static PendingConfirmationResult? MatchPendingHyperBoost(
        WriteConfirmationExpectation pending,
        ConfirmedDeviceState confirmed)
    {
        if (pending.HyperBoostActive is not bool expected)
        {
            return null;
        }

        if (confirmed.HyperBoostActive is not bool observed)
        {
            // Unknown activity is never evidence against the pending write.
            return PendingConfirmationResult.Unavailable;
        }

        if (observed != expected)
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
            } && (restoredPerformance == pending.HyperBoostBaseMode
                // A battery exit confirms as BatteryBalanced on models with a DC preset.
                || pending is { HyperBoostBaseMode: { } requestedBase, HyperBoostExitPowerSource: { } exitSource }
                    && HyperBoostCapabilityPolicy.IsRestoredBase(requestedBase, restoredPerformance, exitSource))
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
        if (pending.GpuClockTuningOnly)
        {
            return MatchGpuClockTuning(pending.Target.GpuClockTuning!, confirmed);
        }
        FanMode desiredFans = pending.Target.FanControl?.Mode
            ?? pending.Target.Fans.Value;
        // Manual thermal modes do not expose a separate performance preset. This is
        // the same semantic match used by ControlCoordinator, which still compares
        // the GPU half below.
        if (!(DeviceSubsystemOrder.IsManualFanMode(desiredFans)
            && DeviceSubsystemOrder.IsManualFanMode(confirmed.Fans)))
        {
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
        }

        if (pending.Target.GpuClockTuning is { } tuning)
        {
            return MatchGpuClockTuning(tuning, confirmed);
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
            ? null
            : before.Current == after.Current
                ? PendingConfirmationResult.Match
                : PendingConfirmationResult.Mismatch;
    }

    private static PendingConfirmationResult? MatchGpuClockTuning(
        GpuClockTuningSettings tuning,
        ConfirmedDeviceState confirmed) => !tuning.Enabled
            ? confirmed.GpuClockOffsets is null ? null : PendingConfirmationResult.Mismatch
            : confirmed.GpuClockOffsets is null
                ? null
                : confirmed.GpuClockOffsets.Current == tuning.EffectiveOffsets
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch;

    private static PendingConfirmationResult MatchPendingBattery(
        WriteConfirmationExpectation pending, ConfirmedBatteryState? battery)
    {
        // Only a recorded limiter-only operation may confirm its own fields
        // without an override reading. Missing/unknown scope retains strictness.
        bool policyOnly = pending.BatteryConfirmation == BatteryConfirmationScope.PolicyOnly
            && !pending.Target.Battery.TemporaryFullCharge;
        if (battery is null || !policyOnly && battery.TemporaryFullCharge is null)
        {
            return PendingConfirmationResult.Unavailable;
        }
        return BatteryMatchesDesired(battery, pending.Target.Battery)
            && (policyOnly && battery.TemporaryFullCharge is null
                || battery.TemporaryFullCharge == pending.Target.Battery.TemporaryFullCharge)
                ? PendingConfirmationResult.Match : PendingConfirmationResult.Mismatch;
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
    // The non-GPU fields matched; the GPU offsets have no confirmation evidence.
    GpuClockOffsetsUnconfirmed,
}

internal readonly record struct WriteConfirmationExpectation(
    DesiredDeviceState Target,
    ConfirmedDeviceState PreWriteState,
    bool? HyperBoostActive = null,
    PerformancePreset? HyperBoostBaseMode = null,
    bool GpuClockTuningOnly = false,
    BatteryConfirmationScope BatteryConfirmation = BatteryConfirmationScope.All,
    PowerSource? HyperBoostExitPowerSource = null);

internal enum BatteryConfirmationScope
{
    All = 0,
    PolicyOnly = 1,
}
