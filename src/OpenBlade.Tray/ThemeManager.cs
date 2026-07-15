using Microsoft.UI.Xaml;

namespace OpenBlade.Tray;

public static class ThemeManager
{
    public static void Apply(FrameworkElement root, ThemePreference preference)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.RequestedTheme = ThemeSelectionPolicy.Resolve(preference) switch
        {
            ResolvedTheme.Default => ElementTheme.Default,
            ResolvedTheme.Light => ElementTheme.Light,
            ResolvedTheme.Dark => ElementTheme.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
    }
}
