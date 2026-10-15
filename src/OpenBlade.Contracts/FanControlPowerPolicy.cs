namespace OpenBlade.Contracts;

/// <summary>Fan modes available on each confirmed power source, within device capabilities.</summary>
public static class FanControlPowerPolicy
{
    public static bool IsAvailable(
        PowerSource source,
        FanMode mode,
        bool supportsManualFansOnLimitedPower = true)
    {
        return source switch
        {
            PowerSource.PluggedIn => mode is FanMode.Automatic or FanMode.Fixed
                or FanMode.ManualCurve or FanMode.Maximum,
            PowerSource.UsbC => mode == FanMode.Automatic
                || supportsManualFansOnLimitedPower
                    && mode is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum,
            // Maximum is the legacy representation of a fixed maximum RPM target.
            PowerSource.Battery => mode is FanMode.Automatic or FanMode.Fixed or FanMode.Maximum,
            _ => false,
        };
    }
}
