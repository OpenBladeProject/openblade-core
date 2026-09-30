using OpenBlade.Contracts;

namespace OpenBlade.Service;

public enum CoolingPadCurveDecisionReason
{
    DemandPlanned,
    Stopped,
    InvalidSampleAge,
    StaleSample,
    MissingCpu,
    InvalidCpu,
    MissingActiveGpu,
    InvalidActiveGpu,
}

public sealed record CoolingPadCurveDecision(
    CoolingPadConfiguredRpmDemand? Demand,
    CoolingPadCurveDecisionReason Reason);

/// <summary>
/// Pure OpenBlade policy: independent linear CPU/GPU curves, upward demand quantization.
/// No demand means no planned update, not firmware handback; a prior target may persist.
/// Plausibility bounds are not emergency thermal thresholds or a safety guarantee.
/// </summary>
public static class CoolingPadCurvePlanner
{
    public static CoolingPadCurveDecision Plan(
        CoolingPadDemandCurve cpuCurve,
        CoolingPadDemandCurve gpuCurve,
        FanTemperatureReadings temperatures,
        TimeSpan sampleAge,
        TimeSpan maximumAge,
        bool stopRequested = false)
    {
        ArgumentNullException.ThrowIfNull(cpuCurve);
        ArgumentNullException.ThrowIfNull(gpuCurve);
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        if (stopRequested)
        {
            return None(CoolingPadCurveDecisionReason.Stopped);
        }
        if (sampleAge < TimeSpan.Zero)
        {
            return None(CoolingPadCurveDecisionReason.InvalidSampleAge);
        }
        if (sampleAge >= maximumAge)
        {
            return None(CoolingPadCurveDecisionReason.StaleSample);
        }
        if (temperatures.CpuCelsius is not int cpu)
        {
            return None(CoolingPadCurveDecisionReason.MissingCpu);
        }
        if (cpu is < 0 or > 115)
        {
            return None(CoolingPadCurveDecisionReason.InvalidCpu);
        }

        int demand = Evaluate(cpuCurve, cpu);
        if (!temperatures.GpuInactive)
        {
            if (temperatures.GpuCelsius is not int gpu)
            {
                return None(CoolingPadCurveDecisionReason.MissingActiveGpu);
            }
            if (gpu is < 0 or > 115)
            {
                return None(CoolingPadCurveDecisionReason.InvalidActiveGpu);
            }
            demand = Math.Max(demand, Evaluate(gpuCurve, gpu));
        }

        return new(new CoolingPadConfiguredRpmDemand(demand), CoolingPadCurveDecisionReason.DemandPlanned);
    }

    private static CoolingPadCurveDecision None(CoolingPadCurveDecisionReason reason) => new(null, reason);

    private static int Evaluate(CoolingPadDemandCurve curve, int temperature)
    {
        IReadOnlyList<CoolingPadCurvePoint> points = curve.Points;
        if (temperature <= points[0].TemperatureCelsius)
        {
            return points[0].Demand.Rpm;
        }
        for (int i = 1; i < points.Count; i++)
        {
            CoolingPadCurvePoint upper = points[i];
            if (temperature > upper.TemperatureCelsius)
            {
                continue;
            }
            CoolingPadCurvePoint lower = points[i - 1];
            int width = upper.TemperatureCelsius - lower.TemperatureCelsius;
            int numerator = lower.Demand.Rpm * width
                + (upper.Demand.Rpm - lower.Demand.Rpm) * (temperature - lower.TemperatureCelsius);
            int quantumDenominator = width * 50;
            return ((numerator + quantumDenominator - 1) / quantumDenominator) * 50;
        }

        return points[^1].Demand.Rpm;
    }
}
