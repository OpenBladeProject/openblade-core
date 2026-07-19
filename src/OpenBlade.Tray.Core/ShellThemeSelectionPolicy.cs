namespace OpenBlade.Tray;

public static class ShellThemeSelectionPolicy
{
    public static bool ShouldUseLightTrayIconGlyphs(int? systemUsesLightTheme) =>
        systemUsesLightTheme != 1;
}
