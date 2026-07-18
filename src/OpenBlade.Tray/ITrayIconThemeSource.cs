namespace OpenBlade.Tray;

public interface ITrayIconThemeSource
{
    event EventHandler? TrayIconThemeChanged;

    bool UseLightTrayIconGlyphs { get; }
}
