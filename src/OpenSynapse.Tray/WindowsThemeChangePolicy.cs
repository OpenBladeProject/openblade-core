using Microsoft.Win32;

namespace OpenSynapse.Tray;

public static class WindowsThemeChangePolicy
{
    public static bool ShouldReapply(ThemePreference preference, UserPreferenceCategory category) =>
        preference == ThemePreference.FollowWindows
        && category is UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Window;
}
