using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using WinRT.Interop;

namespace OpenBlade.Tray;

// Intercept a cancel-to-hide request before WinUI creates AppWindow closing
// arguments. The active view owns this registration and releases it on detach.
internal sealed class WindowsWindowCloseHandler : IDisposable
{
    private const uint CloseMessage = 0x0010;
    private const uint NonClientDestroy = 0x0082;
    private const nuint SubclassId = 0x4F42434C; // "OBCL"
    private static readonly ConcurrentDictionary<nint, WindowsWindowCloseHandler> Hosts = new();
    private static readonly SubclassProcedure SubclassCallback = ProcessWindowMessage;

    private readonly nint windowHandle;
    private Func<bool>? tryHideOnClose;
    private bool handlingClose;
    private bool disposed;

    internal WindowsWindowCloseHandler(Window window, Func<bool> tryHideOnClose)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(tryHideOnClose);
        if (!window.DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("A window close handler must attach on its owning UI thread.");
        }
        windowHandle = WindowNative.GetWindowHandle(window);
        this.tryHideOnClose = tryHideOnClose;
        if (windowHandle == 0 || !Hosts.TryAdd(windowHandle, this))
        {
            throw new InvalidOperationException("The native window already has a close handler or is unavailable.");
        }
        try
        {
            if (!SetWindowSubclass(windowHandle, SubclassCallback, SubclassId, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the window close handler.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static bool IsAttached(nint window) => Hosts.ContainsKey(window);

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        tryHideOnClose = null;
        try { _ = RemoveWindowSubclass(windowHandle, SubclassCallback, SubclassId); }
        finally { Hosts.TryRemove(windowHandle, out _); }
    }

    private bool TryHandleClose()
    {
        // Hiding can synchronously dispatch other messages. A nested close must
        // not destroy the shell before the current hide decision completes.
        if (handlingClose) { return true; }
        handlingClose = true;
        try { return tryHideOnClose?.Invoke() == true; }
        finally { handlingClose = false; }
    }

    private static nint ProcessWindowMessage(nint window, uint message, nuint windowParameter,
        nint longParameter, nuint subclassId, nuint referenceData)
    {
        try
        {
            if (Hosts.TryGetValue(window, out WindowsWindowCloseHandler? host))
            {
                if (message == NonClientDestroy) { host.Dispose(); }
                else if (message == CloseMessage && host.TryHandleClose()) { return 0; }
            }
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.WindowsWindowCloseHandler", "WindowMessage", exception);
            // A failed hide must not turn an ordinary close request into
            // destruction of a shell still owned by the application.
            if (message == CloseMessage) { return 0; }
        }
        return DefSubclassProc(window, message, windowParameter, longParameter);
    }

    private delegate nint SubclassProcedure(nint window, uint message, nuint windowParameter,
        nint longParameter, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint windowParameter, nint longParameter);
}
