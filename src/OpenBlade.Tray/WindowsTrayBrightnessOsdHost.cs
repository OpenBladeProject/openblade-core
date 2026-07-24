using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using OpenBlade.Session;
using WinRT.Interop;

namespace OpenBlade.Tray;

internal sealed class WindowsTrayBrightnessOsdHost : IDisposable
{
    private const uint WindowMessageNonClientDestroy = 0x0082;
    private const nuint SubclassId = 0x4F424453; // "OBDS"

    private static readonly ConcurrentDictionary<nint, WindowsTrayBrightnessOsdHost>
        Hosts = new();
    private static readonly SubclassProcedure SubclassCallback = ProcessWindowMessage;

    private readonly nint windowHandle;
    private readonly uint brightnessChangedMessage;
    private readonly Action<Exception>? errorHandler;
    private IDisplayBrightnessOsdPresenter? presenter;
    private int disposed;

    private WindowsTrayBrightnessOsdHost(
        Window owner,
        SessionAgentEventEndpoint endpoint,
        Action<Exception>? errorHandler)
    {
        this.errorHandler = errorHandler;
        windowHandle = WindowNative.GetWindowHandle(owner);
        if (windowHandle == 0)
        {
            throw new InvalidOperationException(
                "The tray window handle is unavailable.");
        }

        brightnessChangedMessage = RegisterWindowMessage(
            endpoint.DisplayBrightnessChangedMessageName);
        if (brightnessChangedMessage == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The display brightness notification message could not be registered.");
        }

        if (!Hosts.TryAdd(windowHandle, this))
        {
            throw new InvalidOperationException(
                "A display brightness host is already attached to the tray window.");
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
                "The tray could not listen for display brightness changes.");
        }
    }

    public static WindowsTrayBrightnessOsdHost? CreateBestEffort(
        Window owner,
        SessionAgentEventEndpoint endpoint,
        Action<Exception>? errorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            return new WindowsTrayBrightnessOsdHost(
                owner,
                endpoint,
                errorHandler);
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
        presenter?.Dispose();
        presenter = null;
    }

    private nint ProcessMessage(uint message, nuint windowParameter)
    {
        if (message != brightnessChangedMessage || windowParameter > 100)
        {
            return 0;
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            return 0;
        }

        presenter ??= WindowsDisplayBrightnessOsd.CreateBestEffort(errorHandler);
        presenter.Show(checked((byte)windowParameter));
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
        if (Hosts.TryGetValue(window, out WindowsTrayBrightnessOsdHost? host))
        {
            if (message == host.brightnessChangedMessage)
            {
                return host.ProcessMessage(message, windowParameter);
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
