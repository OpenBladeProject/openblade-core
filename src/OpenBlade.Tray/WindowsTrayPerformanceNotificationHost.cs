using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using OpenBlade.Session;
using WinRT.Interop;

namespace OpenBlade.Tray;

internal sealed class WindowsTrayPerformanceNotificationHost : IDisposable
{
    private const uint WindowMessageNonClientDestroy = 0x0082;
    private const nuint SubclassId = 0x4F42504E; // "OBPN"

    private static readonly ConcurrentDictionary<
        nint,
        WindowsTrayPerformanceNotificationHost> Hosts = new();
    private static readonly SubclassProcedure SubclassCallback = ProcessWindowMessage;

    private readonly nint windowHandle;
    private readonly uint notificationMessage;
    private readonly Action<PerformanceShortcutNotificationSignal> notificationHandler;
    private int disposed;

    private WindowsTrayPerformanceNotificationHost(
        Window owner,
        SessionAgentEventEndpoint endpoint,
        Action<PerformanceShortcutNotificationSignal> notificationHandler)
    {
        windowHandle = WindowNative.GetWindowHandle(owner);
        if (windowHandle == 0)
        {
            throw new InvalidOperationException(
                "The tray window handle is unavailable.");
        }

        notificationMessage = RegisterWindowMessage(
            endpoint.PerformanceShortcutNotificationMessageName);
        if (notificationMessage == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The performance notification message could not be registered.");
        }

        this.notificationHandler = notificationHandler;
        if (!Hosts.TryAdd(windowHandle, this))
        {
            throw new InvalidOperationException(
                "A performance notification host is already attached to the tray window.");
        }

        if (!SetWindowSubclass(
                windowHandle,
                SubclassCallback,
                SubclassId,
                0))
        {
            Hosts.TryRemove(windowHandle, out _);
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The tray could not listen for performance notifications.");
        }
    }

    public static WindowsTrayPerformanceNotificationHost? CreateBestEffort(
        Window owner,
        SessionAgentEventEndpoint endpoint,
        Action<PerformanceShortcutNotificationSignal> notificationHandler,
        Action<Exception>? errorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(notificationHandler);
        try
        {
            return new WindowsTrayPerformanceNotificationHost(
                owner,
                endpoint,
                notificationHandler);
        }
        catch (Exception exception)
        {
            errorHandler?.Invoke(exception);
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        _ = RemoveWindowSubclass(windowHandle, SubclassCallback, SubclassId);
        Hosts.TryRemove(windowHandle, out _);
    }

    private nint ProcessMessage(nuint windowParameter)
    {
        if (Volatile.Read(ref disposed) != 0
            || !PerformanceShortcutNotificationSignal.TryDecode(
                windowParameter,
                out PerformanceShortcutNotificationSignal signal))
        {
            return 0;
        }

        notificationHandler(signal);
        return 1;
    }

    private static nint ProcessWindowMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter,
        nuint subclassId,
        nuint referenceData)
    {
        if (Hosts.TryGetValue(
                window,
                out WindowsTrayPerformanceNotificationHost? host))
        {
            if (message == host.notificationMessage)
            {
                return host.ProcessMessage(windowParameter);
            }

            if (message == WindowMessageNonClientDestroy)
            {
                Hosts.TryRemove(window, out _);
                _ = RemoveWindowSubclass(window, SubclassCallback, SubclassId);
            }
        }

        return DefSubclassProc(window, message, windowParameter, longParameter);
    }

    private delegate nint SubclassProcedure(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter,
        nuint subclassId,
        nuint referenceData);

    [DllImport(
        "user32.dll",
        EntryPoint = "RegisterWindowMessageW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint window,
        SubclassProcedure subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint window,
        SubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter);
}
