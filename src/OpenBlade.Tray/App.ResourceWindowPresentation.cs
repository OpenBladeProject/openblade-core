#if OPENBLADE_DEVELOPER_HARNESS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace OpenBlade.Tray;

public partial class App
{
    private static int resourceOffscreenShows;
    private static int resourceOffscreenVerifications;
    private static ResourceDesktopIdentity? resourcePresentationDesktop;
    private static ResourceWindowRectangle? resourcePresentationRectangle;

    // Disconnected developer windows only. The caller owns normal disposal and
    // must call VerifyOffscreenResourceWindow again after its loaded-layout wait.
    private static void ShowOffscreenResourceWindow(Window window, bool requireInputDesktop)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.DispatcherQueue.HasThreadAccess || window.AppWindow.IsVisible)
        {
            throw new InvalidOperationException("An offscreen resource window must be hidden and owned by the current UI thread.");
        }
        resourcePresentationDesktop = ReadResourceDesktopIdentity(requireInputDesktop);
        PrepareNoninteractiveSnapshotWindow(window);
        nint handle = WindowNative.GetWindowHandle(window);
        // Initialize x:Bind explicitly; never use Window.Activate for this workload.
        switch (window)
        {
            case MainWindow main:
                main.InitializeResourceWindowBindings();
                break;
            case TrayDashboardWindow dashboard:
                dashboard.InitializeResourceWindowBindings();
                break;
        }
        ResourceWindowRectangle screen = ReadResourceVirtualScreen();
        const uint noSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
        if (!ResourceWindowNative.SetWindowPos(handle, 0,
            checked(screen.Right + 128), checked(screen.Bottom + 128), 0, 0,
            noSizeNoZOrderNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not place the resource window outside the virtual screen.");
        }
        VerifyOffscreenResourceWindow(window, requireInputDesktop);
        window.AppWindow.Show(activateWindow: false);
        resourceOffscreenShows++;
        VerifyOffscreenResourceWindow(window, requireInputDesktop);
    }

    private static void VerifyOffscreenResourceWindow(Window window, bool requireInputDesktop)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Resource window verification requires its owning UI thread.");
        }
        resourcePresentationDesktop = ReadResourceDesktopIdentity(requireInputDesktop);
        nint handle = WindowNative.GetWindowHandle(window);
        if (!ResourceWindowNative.GetWindowRect(handle, out ResourceWindowNative.Rect rectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the resource window rectangle.");
        }
        ResourceWindowRectangle screen = ReadResourceVirtualScreen();
        if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top
            || !(rectangle.Right <= screen.Left || rectangle.Left >= screen.Right
                || rectangle.Bottom <= screen.Top || rectangle.Top >= screen.Bottom))
        {
            throw new InvalidOperationException("A resource window overlaps the complete virtual screen or has an invalid rectangle.");
        }
        // Foreground can be null on a private desktop; thread-local activation
        // must also remain absent there.
        if (ResourceWindowNative.GetActiveWindow() != 0)
        {
            throw new InvalidOperationException("The resource workload unexpectedly activated a window on its UI thread.");
        }
        VerifyResourceForegroundOwner();
        if (((UIElement)window.Content).IsHitTestVisible)
        {
            throw new InvalidOperationException("A resource window unexpectedly accepts XAML hit testing.");
        }
        resourcePresentationRectangle = new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        resourceOffscreenVerifications++;
    }

    private static void VerifyResourceForegroundOwner()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            nint foreground = WindowsPopupPositioner.GetForegroundWindowHandle();
            if (foreground == 0) { return; }
            if (ResourceWindowNative.GetWindowThreadProcessId(foreground, out uint processId) != 0)
            {
                if (processId == Environment.ProcessId)
                {
                    throw new InvalidOperationException("The resource workload unexpectedly owns a foreground window.");
                }
                return;
            }
            int error = Marshal.GetLastWin32Error();
            // A foreign foreground window can close between these two queries.
            // Retry only a confirmed change, once; unknown ownership still fails.
            if (attempt != 0 || WindowsPopupPositioner.GetForegroundWindowHandle() == foreground)
            {
                throw new Win32Exception(error, "Could not identify the current foreground window owner.");
            }
        }
    }

    private static object SnapshotResourceWindowPresentation() => new
    {
        ShowCount = resourceOffscreenShows,
        VerificationCount = resourceOffscreenVerifications,
        Desktop = resourcePresentationDesktop,
        LastWindowRectangle = resourcePresentationRectangle,
        Presentation = "offscreen AppWindow.Show(false); explicit x:Bind initialization; no activation",
    };

    private static ResourceWindowRectangle ReadResourceVirtualScreen()
    {
        int x = ResourceWindowNative.GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        int y = ResourceWindowNative.GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        int width = ResourceWindowNative.GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        int height = ResourceWindowNative.GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The complete virtual screen geometry is unavailable.");
        }
        return new(x, y, checked(x + width), checked(y + height));
    }

    private static ResourceDesktopIdentity ReadResourceDesktopIdentity(bool requireInputDesktop)
    {
        uint threadId = ResourceWindowNative.GetCurrentThreadId();
        nint current = ResourceWindowNative.GetThreadDesktop(threadId);
        if (current == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the UI thread desktop.");
        }
        // GetThreadDesktop returns a borrowed handle; only OpenInputDesktop is closed.
        string currentName = ReadResourceDesktopName(current);
        string? expectedPrivateDesktop = Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_EXPECTED_DESKTOP");
        if (!requireInputDesktop && (string.IsNullOrEmpty(expectedPrivateDesktop)
            || !string.Equals(currentName, expectedPrivateDesktop, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The private offscreen control requires the exact desktop supplied by its isolated launcher.");
        }
        nint input = ResourceWindowNative.OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS only
        if (input == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (requireInputDesktop)
            {
                throw new Win32Exception(error, "Cannot verify the input desktop for the active-desktop resource workload.");
            }
            // The shared launcher supplies its exact owned desktop name. CI may
            // not grant read access to the protected input desktop.
            return new(threadId, currentName, null, false, error, null);
        }
        Exception? queryFailure = null;
        try
        {
            string inputName = ReadResourceDesktopName(input);
            bool matches = string.Equals(currentName, inputName, StringComparison.OrdinalIgnoreCase);
            if (matches != requireInputDesktop)
            {
                throw new InvalidOperationException("The resource workload's current/input desktop relationship does not match the requested comparison mode.");
            }
            return new(threadId, currentName, inputName, true, null, matches);
        }
        catch (Exception exception)
        {
            queryFailure = exception;
            throw;
        }
        finally
        {
            if (!ResourceWindowNative.CloseDesktop(input))
            {
                var cleanupFailure = new Win32Exception(Marshal.GetLastWin32Error(), "Could not release the input desktop query handle.");
                throw queryFailure is null ? cleanupFailure
                    : new AggregateException("Desktop admission and query-handle cleanup failed.", queryFailure, cleanupFailure);
            }
        }
    }

    private static string ReadResourceDesktopName(nint desktop)
    {
        const int objectName = 2; // UOI_NAME, UTF-16 bytes including the terminator
        _ = ResourceWindowNative.GetUserObjectInformationW(desktop, objectName, 0, 0, out uint bytes);
        if (bytes < 2 || bytes > 65536 || bytes % 2 != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine the desktop name buffer size.");
        }
        nint buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!ResourceWindowNative.GetUserObjectInformationW(desktop, objectName, buffer, bytes, out uint returned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the desktop name.");
            }
            if (returned < 2 || returned > bytes || returned % 2 != 0
                || Marshal.ReadInt16(buffer, checked((int)returned - 2)) != 0)
            {
                throw new InvalidOperationException("The desktop name has invalid length or termination.");
            }
            string name = Marshal.PtrToStringUni(buffer, checked((int)returned / 2 - 1))!;
            return name.Length != 0 && !name.Contains('\0')
                ? name : throw new InvalidOperationException("The desktop name is empty or malformed.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed record ResourceDesktopIdentity(uint ThreadId, string CurrentThreadDesktop,
        string? InputDesktop, bool InputDesktopReadable, int? InputDesktopOpenError, bool? MatchesInputDesktop);

    private sealed record ResourceWindowRectangle(int Left, int Top, int Right, int Bottom);

    private static class ResourceWindowNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out Rect rectangle);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        internal static extern nint GetActiveWindow();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(nint desktop);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformationW(nint handle, int index, nint buffer, uint bytes, out uint needed);
    }
}

public sealed partial class MainWindow
{
    internal void InitializeResourceWindowBindings() => Bindings.Initialize();
}

public sealed partial class TrayDashboardWindow
{
    internal void InitializeResourceWindowBindings() => Bindings.Initialize();
}
#endif
