using Microsoft.UI.Xaml;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal static class WindowTitleBarLifetime
{
    internal static void Release(Window window, bool usesXamlTitleBar)
    {
        string stage = "Hide";
        try
        {
            // Permanent teardown only. Hidden native title bars and extended
            // XAML title bars retain composition/GDI resources after Close on
            // the pinned runtime. Reset while the HWND is still valid and hide
            // first so restoring default chrome cannot appear on screen.
            window.AppWindow.Hide();
            if (usesXamlTitleBar)
            {
                stage = "DetachXamlTitleBar";
                window.SetTitleBar(null);
                stage = "DisableExtension";
                window.ExtendsContentIntoTitleBar = false;
            }
            stage = "ResetNativeTitleBar";
            window.AppWindow.TitleBar.ResetToDefault();
        }
        catch (Exception exception)
        {
            // Do not reset after a failed prerequisite, or prevent the caller
            // from closing the window and completing the remaining teardown.
            DiagnosticTrace.TraceException("OpenBlade.Tray.WindowTitleBarLifetime", stage, exception);
        }
    }
}
