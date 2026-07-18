using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum TrayIconState
{
    GenericError,
    AcBalanced,
    AcSilent,
    AcPerformance,
    UsbCBalanced,
    BatteryBalanced,
    BatterySaver,
}

public static class TrayIconStateResolver
{
    public static TrayIconState Resolve(
        bool isConnected,
        bool deviceVerified,
        PowerSource powerSource,
        PerformancePreset? confirmedPerformance)
    {
        if (!isConnected
            || !deviceVerified
            || powerSource == PowerSource.ExternalUnknown
            || confirmedPerformance is null)
        {
            return TrayIconState.GenericError;
        }

        return (powerSource, confirmedPerformance.Value) switch
        {
            (PowerSource.PluggedIn, PerformancePreset.Balanced) =>
                TrayIconState.AcBalanced,
            (PowerSource.PluggedIn, PerformancePreset.Silent) =>
                TrayIconState.AcSilent,
            (PowerSource.PluggedIn, PerformancePreset.Performance) =>
                TrayIconState.AcPerformance,
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
    }
}
