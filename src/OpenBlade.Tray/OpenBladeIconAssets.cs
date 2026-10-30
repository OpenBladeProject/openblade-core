namespace OpenBlade.Tray;

public static class OpenBladeIconAssets
{
    private static readonly IReadOnlyDictionary<TrayIconState, string> TrayFileNames =
        new Dictionary<TrayIconState, string>
        {
            [TrayIconState.AcBalanced] = "ac-balanced",
            [TrayIconState.AcSilent] = "ac-silent",
            [TrayIconState.AcPerformance] = "ac-performance",
            [TrayIconState.AcHyperBoost] = "ac-hyperboost",
            [TrayIconState.AcCustom] = "ac-custom",
            [TrayIconState.UsbCBalanced] = "usb-c-balanced",
            [TrayIconState.BatteryBalanced] = "battery-balanced",
            [TrayIconState.BatterySaver] = "battery-saver",
            [TrayIconState.ExternalPowerDischarging] = "external-power-discharging",
        };

    public static string? ApplicationIconPath
    {
        get
        {
            string path = AssetPath("OpenBlade.ico");
            return File.Exists(path) ? path : null;
        }
    }

    public static Icon LoadTrayIcon(TrayIconState state, bool useLightGlyphs)
    {
        string theme = useLightGlyphs ? "light" : "dark";
        string relativePath = state == TrayIconState.GenericError
            ? Path.Combine("TrayStatus", $"generic-error-{theme}.ico")
            : Path.Combine("Tray", $"{TrayFileNames[state]}-{theme}.ico");
        string path = AssetPath(relativePath);
        return File.Exists(path)
            ? new Icon(path)
            : (Icon)SystemIcons.Application.Clone();
    }

    private static string AssetPath(params string[] segments) => Path.Combine(
        [AppContext.BaseDirectory, "Assets", "Brand", .. segments]);
}
