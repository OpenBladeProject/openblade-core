using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Tray;

internal static class WindowsPopupPositioner
{
    private const int VirtualScreenLeftMetric = 76;
    private const int VirtualScreenTopMetric = 77;
    private const uint SetWindowNoActivate = 0x0010;
    private const uint DisableTransitionsAttribute = 3;
    private static readonly nint NotTopmostWindow = new(-2);

    public static void PositionInFront(
        nint window,
        int x,
        int y,
        int width,
        int height) =>
        Position(
            window,
            NotTopmostWindow,
            x,
            y,
            width,
            height);

    public static void PositionOffscreen(
        nint window,
        int width,
        int height)
    {
        int x = (int)Math.Clamp(
            (long)GetSystemMetrics(VirtualScreenLeftMetric) - width - 1,
            int.MinValue,
            int.MaxValue);
        int y = (int)Math.Clamp(
            (long)GetSystemMetrics(VirtualScreenTopMetric) - height - 1,
            int.MinValue,
            int.MaxValue);
        Position(window, NotTopmostWindow, x, y, width, height);
    }

    public static void DisableTransitions(nint window)
    {
        int disabled = 1;
        int result = DwmSetWindowAttribute(
            window,
            DisableTransitionsAttribute,
            ref disabled,
            sizeof(int));
        Marshal.ThrowExceptionForHR(result);
    }

    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    public static bool TryActivate(nint window) =>
        window != 0
        && (GetForegroundWindow() == window
            || SetForegroundWindow(window)
            && GetForegroundWindow() == window);

    public static void TryRestoreForeground(nint popup, nint previous)
    {
        if (previous != 0
            && previous != popup
            && IsWindow(previous)
            && GetForegroundWindow() == popup)
        {
            _ = SetForegroundWindow(previous);
        }
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        uint attribute,
        ref int value,
        int valueSize);
}
