using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace OpenBlade.Installer.UI;

internal static class InstallerTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    public static bool ShouldUseDarkMode()
    {
        if (SystemParameters.HighContrast)
        {
            return false;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
        return key?.GetValue(AppsUseLightThemeValue) is int appsUseLightTheme
            && appsUseLightTheme == 0;
    }

    public static void Apply(Window window, bool dark)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        Color windowColor = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF7, 0xF7, 0xF7);
        Color inputColor = dark ? Color.FromRgb(0x18, 0x18, 0x18) : Color.FromRgb(0xFA, 0xFA, 0xFA);
        Color textColor = dark ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);
        Color secondaryTextColor = dark ? Color.FromRgb(0xC7, 0xC7, 0xC7) : Color.FromRgb(0x5A, 0x5A, 0x5A);
        Color borderColor = dark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xD0, 0xD0, 0xD0);
        Color buttonColor = dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xEB, 0xEB, 0xEB);
        Color controlHoverColor = dark ? Color.FromRgb(0x48, 0x48, 0x48) : Color.FromRgb(0xE1, 0xE1, 0xE1);
        Color controlPressedColor = dark ? Color.FromRgb(0x52, 0x52, 0x52) : Color.FromRgb(0xD5, 0xD5, 0xD5);
        Color progressTrackColor = dark ? Color.FromRgb(0x15, 0x15, 0x15) : Color.FromRgb(0xD8, 0xD8, 0xD8);

        window.Resources["WindowBrush"] = FrozenBrush(windowColor);
        window.Resources["InputBrush"] = FrozenBrush(inputColor);
        window.Resources["TextBrush"] = FrozenBrush(textColor);
        window.Resources["SecondaryTextBrush"] = FrozenBrush(secondaryTextColor);
        window.Resources["BorderBrush"] = FrozenBrush(borderColor);
        window.Resources["ButtonBrush"] = FrozenBrush(buttonColor);
        window.Resources["ControlHoverBrush"] = FrozenBrush(controlHoverColor);
        window.Resources["ControlPressedBrush"] = FrozenBrush(controlPressedColor);
        window.Resources["ProgressTrackBrush"] = FrozenBrush(progressTrackColor);
        window.Resources["AccentBrush"] = FrozenBrush(Color.FromRgb(0x44, 0xC7, 0x67));

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            int enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) < 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    sizeof(int));
            }
        }
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);
}
