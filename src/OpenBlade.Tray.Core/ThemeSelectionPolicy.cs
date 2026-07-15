namespace OpenBlade.Tray;

public enum ResolvedTheme
{
    Default,
    Light,
    Dark,
}

public static class ThemeSelectionPolicy
{
    public static ResolvedTheme Resolve(ThemePreference preference) => preference switch
    {
        ThemePreference.FollowWindows => ResolvedTheme.Default,
        ThemePreference.Light => ResolvedTheme.Light,
        ThemePreference.Dark => ResolvedTheme.Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(preference)),
    };
}
