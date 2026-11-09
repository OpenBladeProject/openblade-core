namespace OpenBlade.Tray;

public static class TrayIconAssetPath
{
    public static string Resolve(
        TrayIconState state,
        bool useLightGlyphs,
        bool externalPowerDischarging = false)
    {
        string key = state switch
        {
            TrayIconState.AcBalanced => "ac-balanced",
            TrayIconState.AcSilent => "ac-silent",
            TrayIconState.AcPerformance => "ac-performance",
            TrayIconState.AcCustom => "ac-custom",
            TrayIconState.AcHyperBoost => "ac-hyperboost",
            TrayIconState.UsbCBalanced => "usb-c-balanced",
            TrayIconState.BatteryBalanced => "battery-balanced",
            TrayIconState.BatterySaver => "battery-saver",
            _ => "generic-error",
        };
        string folder = key == "generic-error" ? "TrayStatus" : "Tray";
        if (externalPowerDischarging && TrayIconStateResolver.CanShowDischargeBadge(state))
        {
            key += "-discharging";
        }
        string theme = useLightGlyphs ? "light" : "dark";
        return Path.Combine(folder, $"{key}-{theme}.ico");
    }
}
