using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

// Owned by the permanent shell, not its replaceable localized content. Native
// presenter limits avoid the SDK ChangedArgs retained by AppWindow.Changed.
internal sealed class WindowsWindowSizeConstraints : IDisposable
{
    private const uint SettingChanged = 0x001A;
    private const uint WindowPositionChanged = 0x0047;
    private const uint DisplayChanged = 0x007E;
    private const uint NonClientDestroy = 0x0082;
    private const uint DpiChanged = 0x02E0;
    private const nuint SubclassId = 0x4F425343; // "OBSC"
    private static readonly ConcurrentDictionary<nint, WindowsWindowSizeConstraints> Hosts = new();
    private static readonly SubclassProcedure SubclassCallback = ProcessWindowMessage;

    private readonly nint windowHandle;
    private readonly AppWindow appWindow;
    private readonly OverlappedPresenter presenter;
    private readonly double minimumWidthDip;
    private readonly double minimumHeightDip;
    private Limits? applied;
    private bool applying;
    private bool disposed;

    internal WindowsWindowSizeConstraints(Window window, AppWindow appWindow,
        double minimumWidthDip, double minimumHeightDip)
    {
        windowHandle = WindowNative.GetWindowHandle(window);
        this.appWindow = appWindow;
        presenter = appWindow.Presenter as OverlappedPresenter
            ?? throw new InvalidOperationException("Window size constraints require an overlapped presenter.");
        this.minimumWidthDip = minimumWidthDip;
        this.minimumHeightDip = minimumHeightDip;
        if (windowHandle == 0 || !Hosts.TryAdd(windowHandle, this))
        {
            throw new InvalidOperationException("Window size constraints require an unattached native window.");
        }
        try
        {
            if (!SetWindowSubclass(windowHandle, SubclassCallback, SubclassId, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach window size constraints.");
            }
            Refresh();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        _ = RemoveWindowSubclass(windowHandle, SubclassCallback, SubclassId);
        Hosts.TryRemove(windowHandle, out _);
    }

    private void Refresh()
    {
        if (disposed || applying) { return; }
        uint dpi = GetDpiForWindow(windowHandle);
        if (dpi == 0) { return; }
        RectInt32 workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        PixelRect minimum = WindowPlacementPolicy.EnforceMinimumSize(new PixelRect(0, 0, 1, 1),
            minimumWidthDip, minimumHeightDip, checked((int)dpi), workArea.Width, workArea.Height);
        var limits = new Limits(minimum.Width, minimum.Height, workArea.Width, workArea.Height);
        applying = true;
        try
        {
            if (applied != limits)
            {
                // Clear old minimums before shrinking maximums when a monitor
                // or work area becomes smaller. Never form an invalid pair.
                Clear();
                presenter.PreferredMaximumWidth = limits.MaximumWidth;
                presenter.PreferredMaximumHeight = limits.MaximumHeight;
                presenter.PreferredMinimumWidth = limits.MinimumWidth;
                presenter.PreferredMinimumHeight = limits.MinimumHeight;
            }
            // Limits constrain later Resize calls. Also reconcile a rectangle
            // already applied by WM_DPICHANGED or a work-area change.
            // Leave maximized rectangles (including invisible resize borders)
            // and minimized restoration bounds to Windows.
            if (presenter.State == OverlappedPresenterState.Restored)
            {
                SizeInt32 size = appWindow.Size;
                PixelRect constrained = WindowPlacementPolicy.EnforceMinimumSize(
                    new PixelRect(0, 0, size.Width, size.Height), minimumWidthDip, minimumHeightDip,
                    checked((int)dpi), workArea.Width, workArea.Height);
                if (size.Width != constrained.Width || size.Height != constrained.Height)
                {
                    appWindow.Resize(new SizeInt32(constrained.Width, constrained.Height));
                }
            }
            applied = limits;
        }
        finally { applying = false; }
    }

    private void Clear()
    {
        applied = null;
        presenter.PreferredMinimumWidth = null;
        presenter.PreferredMinimumHeight = null;
        presenter.PreferredMaximumWidth = null;
        presenter.PreferredMaximumHeight = null;
    }

    private bool BeginDpiChange()
    {
        if (disposed || applying) { return false; }
        applying = true;
        return true;
    }

    private static nint ProcessWindowMessage(nint window, uint message, nuint windowParameter,
        nint longParameter, nuint subclassId, nuint referenceData)
    {
        Hosts.TryGetValue(window, out WindowsWindowSizeConstraints? host);
        bool dpiTransition = false;
        try
        {
            if (message == NonClientDestroy) { host?.Dispose(); }
            else if (message == DpiChanged)
            {
                dpiTransition = host?.BeginDpiChange() == true;
                // Keep the guard even if a setter fails partway through Clear.
                // Old physical-pixel limits must not constrain WinUI's resize.
                if (dpiTransition) { host!.Clear(); }
            }
        }
        catch (Exception exception) { Trace(exception); }

        nint result;
        try { result = DefSubclassProc(window, message, windowParameter, longParameter); }
        finally
        {
            // DefSubclassProc can synchronously dispatch position changes.
            // Keep those from reinstalling old limits during the DPI resize.
            if (dpiTransition) { host!.applying = false; }
        }
        if (message is DpiChanged or DisplayChanged or SettingChanged or WindowPositionChanged)
        {
            try { host?.Refresh(); }
            catch (Exception exception) { Trace(exception); }
        }
        return result;
    }

    private static void Trace(Exception exception) => DiagnosticTrace.TraceException(
        "OpenBlade.Tray.WindowsWindowSizeConstraints", "Refresh", exception);

    private readonly record struct Limits(int MinimumWidth, int MinimumHeight, int MaximumWidth, int MaximumHeight);

    private delegate nint SubclassProcedure(nint window, uint message, nuint windowParameter,
        nint longParameter, nuint subclassId, nuint referenceData);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint windowParameter, nint longParameter);
}
