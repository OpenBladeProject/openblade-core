using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum ThermalConvergenceObservationKind
{
    Matched,
    AwaitingObservation,
    Candidate,
    ManualTopologyOnly,
    ExternalHyperBoost,
}

internal sealed record ThermalConvergenceAssessment(
    ThermalConvergenceObservationKind Kind,
    ThermalConvergenceContradiction Contradiction)
{
    public static ThermalConvergenceAssessment Matched { get; } = new(
        ThermalConvergenceObservationKind.Matched,
        ThermalConvergenceContradiction.None);
}

internal static class ThermalConvergenceClassifier
{
    public static ThermalConvergenceAssessment Classify(
        DesiredDeviceState desired,
        ConfirmedDeviceState confirmed,
        bool exactManualIntentOwned = false)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(confirmed);
        if (confirmed.HyperBoostActive == true)
        {
            return new(
                ThermalConvergenceObservationKind.ExternalHyperBoost,
                ThermalConvergenceContradiction.None);
        }

        FanMode desiredFans = desired.FanControl?.Mode ?? desired.Fans.Value;
        bool desiredManual = IsManual(desiredFans);
        bool observedManual = IsManual(confirmed.Fans);
        bool bothManual = desiredManual && observedManual;
        bool manualTopologyOnly = bothManual && !exactManualIntentOwned;
        bool observationUnavailable = confirmed.Fans is null;
        ThermalConvergenceContradiction contradiction =
            ThermalConvergenceContradiction.None;

        if (confirmed.Fans is { } observedFans
            && desiredManual != IsManual(observedFans))
        {
            contradiction |= ThermalConvergenceContradiction.FanTopology;
        }

        // Firmware hides the separate performance selector while both sides are
        // in generic manual fan topology. Do not invent a contradiction from a
        // missing or retained performance sample in that state.
        if (!bothManual)
        {
            if (confirmed.Performance is not { } observedPerformance)
            {
                observationUnavailable = true;
            }
            else if (observedPerformance != desired.Performance.Value)
            {
                contradiction |= ThermalConvergenceContradiction.Performance;
            }
            else
            {
                if (desired.Performance.Value == PerformancePreset.Custom
                    && desired.CustomPerformance is { } expectedCustom)
                {
                    if (confirmed.CustomPerformance is null)
                    {
                        observationUnavailable = true;
                    }
                    else if (confirmed.CustomPerformance != expectedCustom)
                    {
                        contradiction |= ThermalConvergenceContradiction.Performance;
                    }
                }
            }
        }

        // GPU clock tuning is independent of the firmware performance selector.
        // Both-manual topology deliberately hides that selector, so its absence
        // must not hide a confirmed GPU-offset contradiction. Outside that
        // topology, keep an unavailable selector readback fail-closed instead of
        // turning a partial observation into an aggregate performance write.
        if ((bothManual || confirmed.Performance is not null)
            && desired.GpuClockTuning is { } tuning)
        {
            if (tuning.Enabled && confirmed.GpuClockOffsets is null)
            {
                observationUnavailable = true;
            }
            else if (confirmed.GpuClockOffsets is { } observedOffsets
                && observedOffsets.Current != tuning.EffectiveOffsets)
            {
                contradiction |= ThermalConvergenceContradiction.Performance;
            }
        }

        if (contradiction != ThermalConvergenceContradiction.None)
        {
            return new(ThermalConvergenceObservationKind.Candidate, contradiction);
        }
        if (manualTopologyOnly)
        {
            return new(
                ThermalConvergenceObservationKind.ManualTopologyOnly,
                ThermalConvergenceContradiction.None);
        }
        return observationUnavailable
            ? new(
                ThermalConvergenceObservationKind.AwaitingObservation,
                ThermalConvergenceContradiction.None)
            : ThermalConvergenceAssessment.Matched;
    }

    private static bool IsManual(FanMode? mode) => mode is
        FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum;
}
