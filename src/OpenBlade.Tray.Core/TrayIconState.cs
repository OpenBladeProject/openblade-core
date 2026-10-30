using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum TrayIconState
{
    GenericError,
    AcBalanced,
    AcSilent,
    AcPerformance,
    AcCustom,
    UsbCBalanced,
    BatteryBalanced,
    BatterySaver,
    AcHyperBoost,
    ExternalPowerDischarging,
}

public static class TrayIconStateResolver
{
    public static TrayIconState Resolve(
        bool isConnected,
        bool deviceVerified,
        PowerSource powerSource,
        PerformancePreset? effectivePerformance,
        bool hyperBoostActive = false,
        bool externalPowerDischarging = false)
    {
        if (!isConnected
            || !deviceVerified
            || powerSource == PowerSource.ExternalUnknown
            || effectivePerformance is null)
        {
            return TrayIconState.GenericError;
        }

        if (hyperBoostActive)
        {
            return powerSource == PowerSource.PluggedIn
                && effectivePerformance is PerformancePreset.Balanced
                    or PerformancePreset.Silent
                    or PerformancePreset.Performance
                    or PerformancePreset.Custom
                ? externalPowerDischarging
                    ? TrayIconState.ExternalPowerDischarging
                    : TrayIconState.AcHyperBoost
                : TrayIconState.GenericError;
        }

        TrayIconState state = (powerSource, effectivePerformance.Value) switch
        {
            (PowerSource.PluggedIn, PerformancePreset.Balanced) =>
                TrayIconState.AcBalanced,
            (PowerSource.PluggedIn, PerformancePreset.Silent) =>
                TrayIconState.AcSilent,
            (PowerSource.PluggedIn, PerformancePreset.Performance) =>
                TrayIconState.AcPerformance,
            (PowerSource.PluggedIn, PerformancePreset.Custom) =>
                TrayIconState.AcCustom,
            (PowerSource.UsbC, PerformancePreset.Balanced) =>
                TrayIconState.UsbCBalanced,
            (PowerSource.Battery, PerformancePreset.Balanced) =>
                TrayIconState.BatteryBalanced,
            (PowerSource.Battery, PerformancePreset.BatteryBalanced) =>
                TrayIconState.BatteryBalanced,
            (PowerSource.Battery, PerformancePreset.BatterySaver) =>
                TrayIconState.BatterySaver,
            _ => TrayIconState.GenericError,
        };
        return externalPowerDischarging && state != TrayIconState.GenericError
            && powerSource is PowerSource.PluggedIn or PowerSource.UsbC
            ? TrayIconState.ExternalPowerDischarging
            : state;
    }
}
