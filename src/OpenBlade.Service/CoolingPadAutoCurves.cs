using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

/// <summary>
/// Synapse "Smart" fan logic for the RC21-0230 pad (product 3907 shared bundle, pinned in
/// docs/cooling-pad.md): module 74967 evaluates these default tables on each system-monitor
/// sample and sends floor(rpm / 50) with 0D/01 while the firmware-owned record runs the
/// profile's Smart preset (setThermalFanCoolingMode(smart.active, 0, 0), i.e. 0D/10 [0, preset, 0]).
/// </summary>
internal static class CoolingPadAutoCurves
{
    // Synapse "cpu" tables, used for the CPU package temperature.
    private static readonly CoolingPadCurvePoint[] CpuQuiet =
        [new(40, 500), new(50, 500), new(85, 1000), new(90, 1600), new(100, 1600)];
    private static readonly CoolingPadCurvePoint[] CpuBalanced =
        [new(40, 1000), new(90, 2200), new(100, 2200)];
    private static readonly CoolingPadCurvePoint[] CpuPerformance =
        [new(40, 1000), new(55, 2000), new(80, 2700), new(100, 2700)];

    // Synapse "gpu" tables. Its cpu_gpu mode evaluates the GPU side with these same tables.
    private static readonly CoolingPadCurvePoint[] GpuQuiet =
        [new(30, 500), new(40, 500), new(75, 1000), new(80, 1600), new(90, 1600)];
    private static readonly CoolingPadCurvePoint[] GpuBalanced =
        [new(30, 1000), new(80, 2200), new(90, 2200)];
    private static readonly CoolingPadCurvePoint[] GpuPerformance =
        [new(30, 1000), new(45, 2000), new(70, 2700), new(90, 2700)];

    internal static IReadOnlyList<CoolingPadCurvePoint> Cpu(CoolingPadAutoProfile profile) => profile switch
    {
        CoolingPadAutoProfile.Quiet => CpuQuiet,
        CoolingPadAutoProfile.Balanced => CpuBalanced,
        CoolingPadAutoProfile.Performance => CpuPerformance,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static IReadOnlyList<CoolingPadCurvePoint> Gpu(CoolingPadAutoProfile profile) => profile switch
    {
        CoolingPadAutoProfile.Quiet => GpuQuiet,
        CoolingPadAutoProfile.Balanced => GpuBalanced,
        CoolingPadAutoProfile.Performance => GpuPerformance,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>The firmware Smart preset Synapse selects for each profile (3, 4 and 5).</summary>
    internal static CoolingPadFanPreset Preset(CoolingPadAutoProfile profile) => profile switch
    {
        CoolingPadAutoProfile.Quiet => CoolingPadFanPreset.Quiet,
        CoolingPadAutoProfile.Balanced => CoolingPadFanPreset.Balanced,
        CoolingPadAutoProfile.Performance => CoolingPadFanPreset.Performance,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>
    /// Synapse's step evaluation: the speed of the point with the smallest temperature strictly
    /// above <paramref name="temperature"/>; no interpolation. Synapse yields no point (and so 0)
    /// at or above the last point; this uses the last point's speed instead.
    /// </summary>
    internal static int Evaluate(IReadOnlyList<CoolingPadCurvePoint> table, int temperature)
    {
        foreach (CoolingPadCurvePoint point in table)
        {
            if (point.TemperatureCelsius > temperature) { return point.Rpm; }
        }
        return table[^1].Rpm;
    }

    /// <summary>
    /// Cpu and Gpu use their own table; Both takes the higher of the two results. A null
    /// GPU reading means the caller confirmed the GPU powered down: Both then follows the
    /// CPU, and a GPU-only source runs at the GPU table's coolest point.
    /// </summary>
    internal static int Target(CoolingPadAutoProfile profile, CoolingPadTemperatureSource source, int? cpu, int? gpu) =>
        source switch
        {
            CoolingPadTemperatureSource.Cpu => Evaluate(Cpu(profile), Required(cpu)),
            CoolingPadTemperatureSource.Gpu => gpu is int g ? Evaluate(Gpu(profile), g) : Gpu(profile)[0].Rpm,
            CoolingPadTemperatureSource.Both => gpu is int g
                ? Math.Max(Evaluate(Cpu(profile), Required(cpu)), Evaluate(Gpu(profile), g))
                : Evaluate(Cpu(profile), Required(cpu)),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    private static int Required(int? temperature) =>
        temperature ?? throw new InvalidOperationException("A selected cooling pad temperature source is unavailable.");
}
