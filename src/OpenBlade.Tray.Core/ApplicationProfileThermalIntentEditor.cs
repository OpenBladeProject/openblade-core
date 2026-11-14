using OpenBlade.Contracts;

namespace OpenBlade.Tray;

// Display bounds may coerce a retained value while opening the editor. Only
// intentional thermal edits may replace saved nullable intent with that display.
public static class ApplicationProfileThermalIntentEditor
{
    public static ApplicationPowerProfileOverride Apply(
        ApplicationPowerProfileOverride current,
        PowerProfile basis,
        PerformancePreset? selectedPerformance,
        FanMode? selectedFans,
        int fixedSpeedRpm,
        bool performanceChanged,
        bool fanModeChanged,
        bool fixedSpeedChanged)
    {
        bool updateFanControl = fanModeChanged || fixedSpeedChanged
            || performanceChanged && selectedFans == FanMode.Fixed;
        return current with
        {
            Performance = performanceChanged ? selectedPerformance : current.Performance,
            CustomPerformance = performanceChanged
                ? selectedPerformance == PerformancePreset.Custom
                    ? current.CustomPerformance ?? basis.CustomPerformance : null
                : current.CustomPerformance,
            Fans = fanModeChanged ? selectedFans : current.Fans,
            FanControl = updateFanControl
                ? selectedFans is { } mode
                    ? new(mode, fixedSpeedRpm,
                        current.FanControl?.ManualCurve ?? basis.FanControl?.ManualCurve ?? ManualFanCurve.Default)
                    : null
                : current.FanControl,
        };
    }
}
