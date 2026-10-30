namespace OpenBlade.Tray;

public static class OpenBladeIconAssets
{
    public static string? ApplicationIconPath
    {
        get
        {
            string path = AssetPath("OpenBlade.ico");
            return File.Exists(path) ? path : null;
        }
    }

    public static Icon LoadTrayIcon(
        TrayIconState state, bool useLightGlyphs, bool externalPowerDischarging = false)
    {
        string relativePath = TrayIconAssetPath.Resolve(state, useLightGlyphs, externalPowerDischarging);
        string path = AssetPath(relativePath);
        return File.Exists(path)
            ? new Icon(path)
            : (Icon)SystemIcons.Application.Clone();
    }

    private static string AssetPath(params string[] segments) => Path.Combine(
        [AppContext.BaseDirectory, "Assets", "Brand", .. segments]);
}
