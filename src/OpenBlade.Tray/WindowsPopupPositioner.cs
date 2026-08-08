using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Tray;

internal static class WindowsPopupPositioner
{
    private const uint SetWindowNoActivate = 0x0010;
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private static readonly nint TopmostWindow = new(-1);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<nint, nint> TaskbarByMonitor = [];

    public static void PositionBehindTaskbar(
        nint window,
        nint taskbar,
        int x,
        int y,
        int width,
        int height) =>
        Position(
            window,
            taskbar == 0 ? TopmostWindow : taskbar,
            x,
            y,
            width,
            height);

    public static void PositionTopmost(
        nint window,
        int x,
        int y,
        int width,
        int height) =>
        Position(window, TopmostWindow, x, y, width, height);

    public static nint FindTaskbarForMonitor(nint monitor)
    {
        lock (CacheLock)
        {
            if (TaskbarByMonitor.TryGetValue(monitor, out nint cached))
            {
                if (IsTaskbarForMonitor(cached, monitor))
                {
                    return cached;
                }

                TaskbarByMonitor.Remove(monitor);
            }

            nint taskbar = FindWindow(PrimaryTaskbarClass, null);
            if (!IsTaskbarForMonitor(taskbar, monitor))
            {
                taskbar = FindSecondaryTaskbarForMonitor(monitor);
            }

            if (taskbar != 0)
            {
                TaskbarByMonitor[monitor] = taskbar;
            }

            return taskbar;
        }
    }

    private static nint FindSecondaryTaskbarForMonitor(nint monitor)
    {
        nint taskbar = 0;
        while (true)
        {
            taskbar = FindWindowEx(
                0,
                taskbar,
                SecondaryTaskbarClass,
                null);
            if (taskbar == 0 || IsTaskbarForMonitor(taskbar, monitor))
            {
                return taskbar;
            }
        }
    }

    private static bool IsTaskbarForMonitor(nint window, nint monitor)
    {
        if (window == 0 || !IsWindow(window))
        {
            return false;
        }

        var className = new StringBuilder(64);
        if (GetClassName(window, className, className.Capacity) == 0)
        {
            return false;
        }

        string value = className.ToString();
        return (string.Equals(
                    value,
                    PrimaryTaskbarClass,
                    StringComparison.Ordinal)
                || string.Equals(
                    value,
                    SecondaryTaskbarClass,
                    StringComparison.Ordinal))
            && MonitorFromWindow(window, 0) == monitor;
    }

    private static void Position(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height)
    {
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(
        string className,
        string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(
        nint parent,
        nint childAfter,
        string className,
        string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint window,
        StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
}
