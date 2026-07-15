using System.Windows;
using Microsoft.Win32;

namespace OpenBlade.Tray;

public static class ThemeManager
{
    public static void Apply(ThemePreference preference)
    {
        bool useDark = preference == ThemePreference.Dark
            || (preference == ThemePreference.FollowWindows && WindowsUsesDarkApps());
        ResourceDictionary theme = new()
        {
            Source = new Uri(useDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        System.Windows.Application.Current.Resources.MergedDictionaries.Clear();
        System.Windows.Application.Current.Resources.MergedDictionaries.Add(theme);
    }

    private static bool WindowsUsesDarkApps()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
