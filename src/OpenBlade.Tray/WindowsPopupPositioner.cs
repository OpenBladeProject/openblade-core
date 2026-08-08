using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Tray;

internal static class WindowsPopupPositioner
{
    private const uint SetWindowNoActivate = 0x0010;

    public static void PositionBehindTaskbar(
        nint window,
        nint taskbar,
        int x,
        int y,
        int width,
        int height)
    {
        nint insertAfter = taskbar == 0
            ? new nint(-1) // HWND_TOPMOST
            : taskbar;
        if (!SetWindowPos(
                window,
                insertAfter,
                x,
                y,
                width,
                height,
                SetWindowNoActivate))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The popup window could not be positioned.");
        }
    }

    public static nint FindTaskbarForMonitor(nint monitor)
    {
        nint match = 0;
        _ = EnumWindows(
            (candidate, _) =>
            {
                var className = new StringBuilder(64);
                if (GetClassName(candidate, className, className.Capacity) == 0)
                {
                    return true;
                }

                string value = className.ToString();
                if ((string.Equals(
                        value,
                        "Shell_TrayWnd",
                        StringComparison.Ordinal)
                    || string.Equals(
                        value,
                        "Shell_SecondaryTrayWnd",
                        StringComparison.Ordinal))
                    && MonitorFromWindow(candidate, 0) == monitor)
                {
                    match = candidate;
                    return false;
                }

                return true;
            },
            0);
        return match;
    }

    private delegate bool EnumWindowsProcedure(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProcedure callback,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint window,
        StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
}
