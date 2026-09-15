using System.Collections.Frozen;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

// Called under the runtime operation gate; this tracker performs no device access.
internal sealed class ResumeStateValidationTracker
{
    // Allow slow HID re-enumeration without carrying resume attribution into a
    // later, unrelated device or power lifecycle.
    internal static readonly TimeSpan ObservationWindow = TimeSpan.FromMinutes(1);

    private readonly TimeProvider timeProvider;
    private ResumeStateBaseline? pendingBaseline;

    internal ResumeStateValidationTracker(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    internal bool ResumeStarted => pendingBaseline?.ResumeStarted == true;

    internal void Capture(
        PowerSource powerSource,
        ConfirmedDeviceState? confirmed,
        long? lowPowerGeneration = null,
        IReadOnlyCollection<DeviceSubsystem>? unconfirmedWrites = null)
    {
        pendingBaseline = powerSource != PowerSource.ExternalUnknown
            && confirmed is not null
            ? new ResumeStateBaseline(
                powerSource,
                confirmed,
                ResumeStarted: false,
                ResumeLifecycleCompleted: false,
                LightingRestoredDuringResume: false,
                LightingWriteApplied: false,
                Observed: null,
                ObservationWindowStartedAt: null,
                UnconfirmedWrites: NormalizeUnconfirmedWrites(unconfirmedWrites),
                ConfirmedWrites: FrozenSet<DeviceSubsystem>.Empty,
                LowPowerGeneration: lowPowerGeneration)
            : null;
    }

    internal void RebaseBeforeResume(
        long lowPowerGeneration,
        PowerSource powerSource,
        ConfirmedDeviceState? confirmed,
        IReadOnlyCollection<DeviceSubsystem>? unconfirmedWrites = null)
    {
        if (pendingBaseline is not { ResumeStarted: false } baseline
            || baseline.LowPowerGeneration != lowPowerGeneration)
        {
            return;
        }

        // Thermal lifecycle cleanup is intentional. Keep the keyboard and logo
        // sample from before standby Off while adopting its confirmed result.
        // Pending writes leave cached readings unsuitable for retention
        // comparison, even when a later resume observation resolves the write.
        pendingBaseline = powerSource != PowerSource.ExternalUnknown
            && confirmed is not null
            ? baseline with
            {
                PowerSource = powerSource,
                UnconfirmedWrites = UnionUnconfirmedWrites(baseline, unconfirmedWrites),
                // These fields now describe the confirmed cleanup result. Only
                // lighting still uses its original, pre-Off sample.
                ConfirmedWrites = baseline.ConfirmedWrites
                    .Where(subsystem => subsystem == DeviceSubsystem.Lighting)
                    .ToFrozenSet(),
                Confirmed = confirmed with
                {
                    Lighting = baseline.Confirmed.Lighting,
                    LogoLighting = baseline.Confirmed.LogoLighting,
                },
            }
            : null;
    }

    internal void Begin(
        bool gpuClockOffsetConfirmationInvalidated,
        IReadOnlyCollection<DeviceSubsystem>? unconfirmedWrites = null)
    {
        if (pendingBaseline is { } baseline)
        {
            pendingBaseline = baseline with
            {
                ResumeStarted = true,
                UnconfirmedWrites = UnionUnconfirmedWrites(baseline, unconfirmedWrites),
                GpuClockOffsetConfirmationInvalidated =
                    baseline.GpuClockOffsetConfirmationInvalidated
                    || gpuClockOffsetConfirmationInvalidated,
            };
        }
    }

    internal void Cancel() => pendingBaseline = null;

    internal void RecordPendingWrite(DeviceSubsystem subsystem)
    {
        if (pendingBaseline is { } baseline
            && ResumeComparedSubsystems.Contains(subsystem)
            && !baseline.UnconfirmedWrites.Contains(subsystem))
        {
            pendingBaseline = baseline with
            {
                UnconfirmedWrites = UnionUnconfirmedWrites(baseline, [subsystem]),
            };
        }
    }

    internal void RecordConfirmedWrite(DeviceSubsystem subsystem)
    {
        if (pendingBaseline is { } baseline
            && ResumeComparedSubsystems.Contains(subsystem)
            && !baseline.ConfirmedWrites.Contains(subsystem))
        {
            pendingBaseline = baseline with
            {
                ConfirmedWrites = baseline.ConfirmedWrites.Append(subsystem).ToFrozenSet(),
            };
        }
    }

    internal ResumeStateValidationResult? Complete()
    {
        if (pendingBaseline is not { ResumeStarted: true } baseline)
        {
            return null;
        }

        ResumeStateBaseline completed = baseline with
        {
            ResumeLifecycleCompleted = true,
            ObservationWindowStartedAt = timeProvider.GetTimestamp(),
        };
        pendingBaseline = completed;
        return completed.Observed is { } observed
            ? ConsumeComparison(completed, observed)
            : null;
    }

    internal void RecordLightingRestore(bool writeApplied)
    {
        if (pendingBaseline is { ResumeStarted: true } baseline)
        {
            pendingBaseline = baseline with
            {
                LightingRestoredDuringResume = true,
                LightingWriteApplied = writeApplied,
            };
        }
    }

    internal void DiscardUnstarted()
    {
        if (pendingBaseline is { ResumeStarted: false })
        {
            pendingBaseline = null;
        }
    }

    internal void DiscardExpired()
    {
        if (pendingBaseline is
            {
                ResumeLifecycleCompleted: true,
                ObservationWindowStartedAt: { } startedAt,
            }
            && timeProvider.GetElapsedTime(startedAt) >= ObservationWindow)
        {
            pendingBaseline = null;
        }
    }

    internal ResumeStateValidationResult? RecordPowerSourceTransition(PowerSource powerSource)
    {
        if (pendingBaseline is not { ResumeStarted: true } baseline)
        {
            return null;
        }

        pendingBaseline = null;
        return baseline.ResumeLifecycleCompleted
            ? null
            : new ResumeStateValidationResult(
                ControlOperationStatus.Stored,
                $"Resume changed the power source from {baseline.PowerSource} to {powerSource}; the existing destination-profile transition was used instead of comparing the two power profiles.");
    }

    internal ResumeStateValidationResult? Observe(
        PowerSource powerSource,
        ConfirmedDeviceState observed)
    {
        if (pendingBaseline is not { ResumeStarted: true } baseline)
        {
            return null;
        }

        if (baseline.PowerSource != powerSource)
        {
            return RecordPowerSourceTransition(powerSource);
        }

        if (!baseline.ResumeLifecycleCompleted)
        {
            pendingBaseline = baseline with { Observed = observed };
            return null;
        }

        return ConsumeComparison(baseline, observed);
    }

    private ResumeStateValidationResult? ConsumeComparison(
        ResumeStateBaseline baseline,
        ConfirmedDeviceState observed)
    {
        if (pendingBaseline != baseline)
        {
            return null;
        }

        pendingBaseline = null;
        IReadOnlyList<DeviceSubsystem> compared = GetComparableResumeSubsystems(
            baseline,
            observed);
        IReadOnlyList<DeviceSubsystem> changed = GetChangedResumeSubsystems(
            baseline.Confirmed,
            observed,
            compared);
        if (changed.Count > 0)
        {
            return new ResumeStateValidationResult(
                ControlOperationStatus.Blocked,
                BuildResumeValidationSummary(
                    $"Resume validation observed changed control state for {DeviceSubsystemFormatter.FormatList(changed)}. The changes remain externally classified; no validation-driven recovery writes were attempted.",
                    baseline,
                    observed,
                    compared));
        }

        return new ResumeStateValidationResult(
            baseline.LightingWriteApplied
                ? ControlOperationStatus.Applied
                : ControlOperationStatus.Stored,
            BuildResumeValidationSummary(
                compared.Count > 0
                    ? $"Resume validation confirmed retained control state for {DeviceSubsystemFormatter.FormatList(compared)}. No validation-driven recovery writes were needed."
                    : "Resume validation had no eligible control state to compare. No validation-driven recovery writes were attempted.",
                baseline,
                observed,
                compared));
    }

    private static string BuildResumeValidationSummary(
        string result,
        ResumeStateBaseline baseline,
        ConfirmedDeviceState observed,
        IReadOnlyList<DeviceSubsystem> compared)
    {
        var details = new List<string> { result };
        if (baseline.UnconfirmedWrites.Count > 0)
        {
            string unconfirmed = DeviceSubsystemFormatter.FormatList(
                ResumeComparedSubsystems.Where(baseline.UnconfirmedWrites.Contains));
            details.Add($"Not compared because OpenBlade had unconfirmed writes during the suspend or resume lifecycle: {unconfirmed}.");
        }
        if (baseline.ConfirmedWrites.Count > 0)
        {
            string written = DeviceSubsystemFormatter.FormatList(
                ResumeComparedSubsystems.Where(baseline.ConfirmedWrites.Contains));
            details.Add($"Not compared because OpenBlade confirmed control writes after the suspend baseline was captured: {written}.");
        }
        if (baseline.LightingRestoredDuringResume)
        {
            details.Add(baseline.LightingWriteApplied
                ? "Lighting was not compared because OpenBlade restored its owned lighting during the resume lifecycle; that restoration applied a lighting control write."
                : "Lighting was not compared because OpenBlade restored its owned lighting during the resume lifecycle; no new lighting control write was required.");
        }
        else if (!compared.Contains(DeviceSubsystem.Lighting)
            && !baseline.UnconfirmedWrites.Contains(DeviceSubsystem.Lighting)
            && !baseline.ConfirmedWrites.Contains(DeviceSubsystem.Lighting))
        {
            details.Add("Lighting was unavailable in at least one sample and was not compared.");
        }

        IReadOnlyList<DeviceSubsystem> unavailable = GetUnavailableResumeSubsystems(
            compared,
            baseline.UnconfirmedWrites,
            baseline.ConfirmedWrites,
            excludeLighting: baseline.LightingRestoredDuringResume);
        if (unavailable.Count > 0)
        {
            details.Add($"Unavailable for comparison: {DeviceSubsystemFormatter.FormatList(unavailable)}.");
        }
        return string.Join(" ", details);
    }

    private static IReadOnlyList<DeviceSubsystem> GetComparableResumeSubsystems(
        ResumeStateBaseline baseline,
        ConfirmedDeviceState observed)
    {
        var compared = new List<DeviceSubsystem>(4);
        if (!baseline.UnconfirmedWrites.Contains(DeviceSubsystem.Performance)
            && !baseline.ConfirmedWrites.Contains(DeviceSubsystem.Performance)
            && (baseline.Confirmed.Performance is not null
                || baseline.Confirmed.HyperBoostActive)
            && (observed.Performance is not null
                || observed.HyperBoostActive)
            && !baseline.GpuClockOffsetConfirmationInvalidated
            && (baseline.Confirmed.GpuClockOffsets is null
                || observed.GpuClockOffsets is not null))
        {
            compared.Add(DeviceSubsystem.Performance);
        }
        if (!baseline.UnconfirmedWrites.Contains(DeviceSubsystem.Fans)
            && !baseline.ConfirmedWrites.Contains(DeviceSubsystem.Fans)
            && baseline.Confirmed.Fans is not null && observed.Fans is not null)
        {
            compared.Add(DeviceSubsystem.Fans);
        }
        if (!baseline.UnconfirmedWrites.Contains(DeviceSubsystem.Battery)
            && !baseline.ConfirmedWrites.Contains(DeviceSubsystem.Battery)
            && baseline.Confirmed.Battery is not null && observed.Battery is not null)
        {
            compared.Add(DeviceSubsystem.Battery);
        }
        if (!baseline.UnconfirmedWrites.Contains(DeviceSubsystem.Lighting)
            && !baseline.ConfirmedWrites.Contains(DeviceSubsystem.Lighting)
            && !baseline.LightingRestoredDuringResume
            && baseline.Confirmed.Lighting is not null
            && observed.Lighting is not null)
        {
            compared.Add(DeviceSubsystem.Lighting);
        }
        return compared;
    }

    private static IReadOnlyList<DeviceSubsystem> GetUnavailableResumeSubsystems(
        IReadOnlyList<DeviceSubsystem> compared,
        IReadOnlySet<DeviceSubsystem> unconfirmedWrites,
        IReadOnlySet<DeviceSubsystem> confirmedWrites,
        bool excludeLighting)
    {
        var unavailable = new List<DeviceSubsystem>(4);
        foreach (DeviceSubsystem subsystem in ResumeComparedSubsystems)
        {
            if ((!excludeLighting || subsystem != DeviceSubsystem.Lighting)
                && !unconfirmedWrites.Contains(subsystem)
                && !confirmedWrites.Contains(subsystem)
                && !compared.Contains(subsystem))
            {
                unavailable.Add(subsystem);
            }
        }
        return unavailable;
    }

    private static FrozenSet<DeviceSubsystem> UnionUnconfirmedWrites(
        ResumeStateBaseline baseline,
        IReadOnlyCollection<DeviceSubsystem>? unconfirmedWrites) =>
        NormalizeUnconfirmedWrites(baseline.UnconfirmedWrites.Concat(unconfirmedWrites ?? []));

    private static FrozenSet<DeviceSubsystem> NormalizeUnconfirmedWrites(
        IEnumerable<DeviceSubsystem>? unconfirmedWrites)
    {
        var pending = new HashSet<DeviceSubsystem>(
            unconfirmedWrites?.Where(ResumeComparedSubsystems.Contains) ?? []);
        bool thermalPending = pending.Contains(DeviceSubsystem.Performance)
            || pending.Contains(DeviceSubsystem.Fans);
        // Only the four compared subsystems can enter the immutable baseline.
        // Performance and fan writes can change coupled thermal state.
        return ResumeComparedSubsystems.Where(subsystem => pending.Contains(subsystem)
                || thermalPending && DeviceSubsystemOrder.IsThermal(subsystem))
            .ToFrozenSet();
    }

    private static IReadOnlyList<DeviceSubsystem> GetChangedResumeSubsystems(
        ConfirmedDeviceState baseline,
        ConfirmedDeviceState observed,
        IReadOnlyList<DeviceSubsystem> compared)
    {
        var changed = new List<DeviceSubsystem>(4);
        foreach (DeviceSubsystem subsystem in compared)
        {
            bool matches = subsystem switch
            {
                DeviceSubsystem.Performance =>
                    baseline.Performance == observed.Performance
                    && baseline.HyperBoostActive == observed.HyperBoostActive
                    && CompareWhenBothKnown(
                        baseline.CustomPerformance,
                        observed.CustomPerformance)
                    && CompareWhenBothKnown(
                        baseline.GpuClockOffsets,
                        observed.GpuClockOffsets),
                DeviceSubsystem.Fans => baseline.Fans == observed.Fans,
                DeviceSubsystem.Battery => baseline.Battery == observed.Battery,
                DeviceSubsystem.Lighting =>
                    baseline.Lighting == observed.Lighting
                    && CompareWhenBothKnown(
                        baseline.LogoLighting,
                        observed.LogoLighting),
                _ => true,
            };
            if (!matches)
            {
                changed.Add(subsystem);
            }
        }
        return changed;
    }

    private static bool CompareWhenBothKnown<T>(T? baseline, T? observed)
        where T : class => baseline is null
            || observed is null
            || EqualityComparer<T>.Default.Equals(baseline, observed);

    private static bool CompareWhenBothKnown<T>(T? baseline, T? observed)
        where T : struct => baseline is null
            || observed is null
            || EqualityComparer<T>.Default.Equals(baseline.Value, observed.Value);

    internal static IReadOnlySet<string> ComparedStatePropertyNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ConfirmedDeviceState.Performance),
            nameof(ConfirmedDeviceState.CustomPerformance),
            nameof(ConfirmedDeviceState.GpuClockOffsets),
            nameof(ConfirmedDeviceState.HyperBoostActive),
            nameof(ConfirmedDeviceState.Fans),
            nameof(ConfirmedDeviceState.Battery),
            nameof(ConfirmedDeviceState.Lighting),
            nameof(ConfirmedDeviceState.LogoLighting),
        };

    private static IReadOnlyList<DeviceSubsystem> ResumeComparedSubsystems { get; } =
    [
        DeviceSubsystem.Performance,
        DeviceSubsystem.Fans,
        DeviceSubsystem.Battery,
        DeviceSubsystem.Lighting,
    ];

    private sealed record ResumeStateBaseline(
        PowerSource PowerSource,
        ConfirmedDeviceState Confirmed,
        bool ResumeStarted,
        bool ResumeLifecycleCompleted,
        bool LightingRestoredDuringResume,
        bool LightingWriteApplied,
        ConfirmedDeviceState? Observed,
        long? ObservationWindowStartedAt,
        FrozenSet<DeviceSubsystem> UnconfirmedWrites,
        FrozenSet<DeviceSubsystem> ConfirmedWrites,
        bool GpuClockOffsetConfirmationInvalidated = false,
        long? LowPowerGeneration = null);
}

internal sealed record ResumeStateValidationResult(
    ControlOperationStatus Status,
    string Message);
