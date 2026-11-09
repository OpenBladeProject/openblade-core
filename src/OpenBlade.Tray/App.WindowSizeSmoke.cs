#if OPENBLADE_DEVELOPER_HARNESS
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

public partial class App
{
    private static async Task RunWindowSizeSmokeAsync(string reportPath)
    {
        Window? window = null;
        WindowsWindowSizeConstraints? constraints = null;
        Exception? failure = null;
        try
        {
            _ = ReadResourceDesktopIdentity(requireInputDesktop: false);
            File.WriteAllText(reportPath, "Native outer-window size constraints on an isolated desktop\n");
            window = new Window { Content = new Grid() };
            PrepareNoninteractiveSnapshotWindow(window);
            AppWindow appWindow = window.AppWindow;
            var presenter = (OverlappedPresenter)appWindow.Presenter;
            nint handle = WindowNative.GetWindowHandle(window);
            constraints = new WindowsWindowSizeConstraints(window, appWindow, 800, 600);
            IReadOnlyList<DisplayArea> areas = DisplayArea.FindAll();
            if (areas.Count == 0) { throw new InvalidOperationException("No display is available for native sizing validation."); }
            for (int areaIndex = 0; areaIndex < areas.Count; areaIndex++)
            {
                DisplayArea area = areas[areaIndex];
                appWindow.Hide();
                appWindow.Move(new PointInt32(area.WorkArea.X + 32, area.WorkArea.Y + 32));
                await DrainDispatcherAsync(window.DispatcherQueue);
                uint dpi = WindowSizeSmokeNative.GetDpiForWindow(handle);
                DisplayArea resolved = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
                if (!resolved.DisplayId.Equals(area.DisplayId))
                {
                    throw new InvalidOperationException("The monitor transition did not reach its requested display.");
                }
                RectInt32 work = resolved.WorkArea;
                PixelRect minimum = WindowPlacementPolicy.EnforceMinimumSize(new PixelRect(0, 0, 1, 1),
                    800, 600, checked((int)dpi), work.Width, work.Height);
                VerifyPresenterLimits(presenter, minimum, work);
                await VerifyResizeAsync(new SizeInt32(100, 100), "hidden minimum");
                appWindow.Show(activateWindow: false);
                if (!WindowSizeSmokeNative.SetWindowPos(handle, 0, 0, 0, 100, 100, 0x0002 | 0x0004 | 0x0010))
                {
                    throw new InvalidOperationException("Could not exercise direct native resizing.");
                }
                await DrainDispatcherAsync(window.DispatcherQueue);
                if (appWindow.Size.Width != minimum.Width || appWindow.Size.Height != minimum.Height)
                {
                    throw new InvalidOperationException("A direct native resize bypassed the minimum size.");
                }
                await VerifyResizeAsync(new SizeInt32(100, Math.Min(work.Height, minimum.Height + 80)), "width minimum");
                await VerifyResizeAsync(new SizeInt32(Math.Min(work.Width, minimum.Width + 80), 100), "height minimum");
                await VerifyResizeAsync(new SizeInt32(work.Width + 200, work.Height + 200), "maximum");
                presenter.Minimize();
                constraints.Dispose();
                constraints = new WindowsWindowSizeConstraints(window, appWindow, 700, 500);
                if (presenter.State != OverlappedPresenterState.Minimized)
                {
                    throw new InvalidOperationException("Refreshing limits restored a minimized window.");
                }
                constraints.Dispose();
                constraints = new WindowsWindowSizeConstraints(window, appWindow, 800, 600);
                presenter.Restore(false);
                await VerifyResizeAsync(new SizeInt32(100, 100), "restored minimum");
                SizeInt32 restoredSize = appWindow.Size;
                presenter.Maximize();
                await DrainDispatcherAsync(window.DispatcherQueue);
                if (presenter.State != OverlappedPresenterState.Maximized
                    || appWindow.ClientSize.Width > work.Width || appWindow.ClientSize.Height > work.Height)
                {
                    throw new InvalidOperationException($"Maximize: state={presenter.State}, size={appWindow.Size.Width}x{appWindow.Size.Height}, work={work.Width}x{work.Height}.");
                }
                presenter.Restore(false);
                await DrainDispatcherAsync(window.DispatcherQueue);
                if (!appWindow.Size.Equals(restoredSize))
                {
                    throw new InvalidOperationException("Maximize changed the saved restoration size.");
                }
                // A same-DPI notification must reinstall cleared limits even
                // when the cached tuple would otherwise compare equal.
                PointInt32 position = appWindow.Position;
                var rectangle = new WindowSizeSmokeNative.Rect(position.X, position.Y,
                    position.X + 100, position.Y + 100);
                nint suggested = Marshal.AllocHGlobal(Marshal.SizeOf<WindowSizeSmokeNative.Rect>());
                try
                {
                    Marshal.StructureToPtr(rectangle, suggested, false);
                    _ = WindowSizeSmokeNative.SendMessage(handle, 0x02E0, (nuint)(dpi | (dpi << 16)), suggested);
                }
                finally { Marshal.FreeHGlobal(suggested); }
                VerifyPresenterLimits(presenter, minimum, work);
                if (appWindow.Size.Width < minimum.Width || appWindow.Size.Height < minimum.Height)
                {
                    throw new InvalidOperationException("DPI reconciliation left the current window below its minimum.");
                }
                await VerifyResizeAsync(new SizeInt32(100, 100), "same-DPI minimum");
                File.AppendAllText(reportPath, $"DPI {dpi}, work area {work.Width}x{work.Height}: passed\n");

                async Task VerifyResizeAsync(SizeInt32 requested, string phase)
                {
                    appWindow.Resize(requested);
                    await DrainDispatcherAsync(window.DispatcherQueue);
                    PixelRect expected = WindowPlacementPolicy.EnforceMinimumSize(
                        new PixelRect(0, 0, requested.Width, requested.Height), 800, 600,
                        checked((int)dpi), work.Width, work.Height);
                    if (appWindow.Size.Width != expected.Width || appWindow.Size.Height != expected.Height)
                    {
                        throw new InvalidOperationException($"{phase}: got {appWindow.Size.Width}x{appWindow.Size.Height}, expected {expected.Width}x{expected.Height}.");
                    }
                }
            }

            // Exercise clamped minima and replacement across a much smaller
            // effective range without changing any monitor configuration.
            constraints.Dispose();
            AssertSizeHostAttached(handle, expected: false);
            constraints = new WindowsWindowSizeConstraints(window, appWindow, 100_000, 100_000);
            RectInt32 currentWork = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            VerifyPresenterLimits(presenter, new PixelRect(0, 0, currentWork.Width, currentWork.Height), currentWork);
            constraints.Dispose();
            constraints = new WindowsWindowSizeConstraints(window, appWindow, 800, 600);
            PixelRect restoredMinimum = WindowPlacementPolicy.EnforceMinimumSize(new PixelRect(0, 0, 1, 1),
                800, 600, checked((int)WindowSizeSmokeNative.GetDpiForWindow(handle)), currentWork.Width, currentWork.Height);
            VerifyPresenterLimits(presenter, restoredMinimum, currentWork);
            AssertSizeHostAttached(handle, expected: true);
            window.Close();
            window = null;
            AssertSizeHostAttached(handle, expected: false);
            constraints.Dispose();
            File.AppendAllText(reportPath, "range replacement and native destruction cleanup: passed\n");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            constraints?.Dispose();
            window?.Close();
        }
        if (failure is not null) { File.AppendAllText(reportPath, failure.ToString()); }
        Environment.Exit(failure is null ? 0 : 1);
    }

    private static void VerifyPresenterLimits(OverlappedPresenter presenter, PixelRect minimum, RectInt32 work)
    {
        if (presenter.PreferredMinimumWidth != minimum.Width || presenter.PreferredMinimumHeight != minimum.Height
            || presenter.PreferredMaximumWidth != work.Width || presenter.PreferredMaximumHeight != work.Height)
        {
            throw new InvalidOperationException("Native presenter limits do not match the DPI/work-area policy.");
        }
    }

    private static void AssertSizeHostAttached(nint handle, bool expected)
    {
        var hosts = (System.Collections.IDictionary)typeof(WindowsWindowSizeConstraints)
            .GetField("Hosts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        if (hosts.Contains(handle) != expected)
        {
            throw new InvalidOperationException("The native sizing callback has the wrong shell lifetime.");
        }
    }

    private static class WindowSizeSmokeNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct Rect(int Left, int Top, int Right, int Bottom);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static extern nint SendMessage(nint window, uint message, nuint windowParameter, nint longParameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    }
}
#endif
