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
}

public static class TrayIconStateResolver
{
    public static TrayIconState Resolve(
        bool isConnected,
        bool deviceVerified,
        PowerSource powerSource,
        PerformancePreset? effectivePerformance,
        bool hyperBoostActive = false)
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
            return PowerSourcePolicy.IsExternal(powerSource)
                && effectivePerformance is PerformancePreset.Balanced
                    or PerformancePreset.Silent
                    or PerformancePreset.Performance
                    or PerformancePreset.Turbo
                    or PerformancePreset.MaximumPower
                    or PerformancePreset.Gaming
                    or PerformancePreset.Creator
                    or PerformancePreset.Custom
                    or PerformancePreset.BatteryBalanced
                    or PerformancePreset.BatterySaver
                ? TrayIconState.AcHyperBoost
                : TrayIconState.GenericError;
        }

        return (powerSource, effectivePerformance.Value) switch
        {
            (PowerSource.PluggedIn, PerformancePreset.Balanced) =>
                TrayIconState.AcBalanced,
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.Silent) =>
                TrayIconState.AcSilent,
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.Performance
                or PerformancePreset.Turbo or PerformancePreset.MaximumPower
                or PerformancePreset.Gaming or PerformancePreset.Creator) =>
                TrayIconState.AcPerformance,
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.Custom) =>
                TrayIconState.AcCustom,
            (PowerSource.UsbC, PerformancePreset.Balanced or PerformancePreset.BatteryBalanced) =>
                TrayIconState.UsbCBalanced,
            (PowerSource.Battery, PerformancePreset.Balanced) =>
                TrayIconState.BatteryBalanced,
            (PowerSource.PluggedIn, PerformancePreset.BatteryBalanced) =>
                TrayIconState.AcBalanced,
            (PowerSource.Battery, PerformancePreset.BatteryBalanced) =>
                TrayIconState.BatteryBalanced,
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.BatterySaver) =>
                TrayIconState.AcSilent,
            (PowerSource.Battery, PerformancePreset.BatterySaver) =>
                TrayIconState.BatterySaver,
            _ => TrayIconState.GenericError,
        };
    }

    public static bool CanShowDischargeBadge(TrayIconState state) => state is
        TrayIconState.AcBalanced or TrayIconState.AcSilent or TrayIconState.AcPerformance
        or TrayIconState.AcCustom or TrayIconState.AcHyperBoost or TrayIconState.UsbCBalanced;
}
