using OpenBlade.Contracts;

namespace OpenBlade.Service;

/// <summary>Cooling policy for the separately admitted Battery Saver stop target.</summary>
internal sealed class BatterySaverFanStopRuntimePolicy
{
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);
    internal static FanControlSettings StopSettings { get; } =
        new(FanMode.Stop, FanControlSettings.Default.FixedSpeedRpm,
            FanControlSettings.Default.ManualCurve);

    private volatile bool thermallyLatched;
    internal bool ThermallyLatched => thermallyLatched;

    internal void Reset()
    {
        thermallyLatched = false;
    }

    // Workload inactivity is required, but cannot establish that an awake GPU
    // has cooled. Reuse the cooling policy's 55 C upper bound for actual GPU data.
    internal static bool IsCool(FanTemperatureReadings readings) =>
        readings.CpuCelsius is >= 0 and <= 55 && readings.GpuInactive
        && (readings.GpuPoweredDown || readings.GpuCelsius is >= 10 and <= 55);

    internal static bool IsInScope(bool requested, DeviceCapabilities? capabilities,
        PowerSource powerSource, PerformancePreset performance) =>
        requested && capabilities?.SupportsBatterySaverFanStop == true
        && powerSource == PowerSource.Battery
        && performance == PerformancePreset.BatterySaver;

    internal FanMode? Resolve(bool inScope, bool cool, bool ownsStop)
    {
        if (!inScope)
        {
            Reset();
            return null;
        }
        if (ownsStop && !cool)
        {
            // The 55 C bound is a product cooling policy from the reviewed probe,
            // not a firmware entry requirement. Once released, firmware keeps
            // cooling authority for this activation even if temperature falls.
            thermallyLatched = true;
        }
        return ThermallyLatched ? FanMode.Automatic : cool ? FanMode.Stop : null;
    }

    internal bool IsSuspended(bool inScope, bool cool) =>
        inScope && (ThermallyLatched || !cool);
}
