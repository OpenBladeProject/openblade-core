using OpenBlade.Contracts;

namespace OpenBlade.Service;

public readonly record struct CoolingPadCurvePoint(
    int TemperatureCelsius,
    CoolingPadConfiguredRpmDemand Demand);

/// <summary>Immutable caller-provided policy; not a saved or vendor preset.</summary>
public sealed class CoolingPadDemandCurve
{
    public CoolingPadDemandCurve(IEnumerable<CoolingPadCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        CoolingPadCurvePoint[] copy = points.Take(11).ToArray();
        if (copy.Length is < 2 or > 10)
        {
            throw new ArgumentException("A curve requires two through ten points.", nameof(points));
        }

        for (int i = 0; i < copy.Length; i++)
        {
            CoolingPadCurvePoint point = copy[i];
            if (point.TemperatureCelsius is < 0 or > 115
                || !point.Demand.IsWellFormed
                || i > 0 && (point.TemperatureCelsius <= copy[i - 1].TemperatureCelsius
                    || point.Demand.Rpm < copy[i - 1].Demand.Rpm))
            {
                throw new ArgumentException("Invalid temperature or demand curve.", nameof(points));
            }
        }

        Points = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<CoolingPadCurvePoint> Points { get; }
}

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
