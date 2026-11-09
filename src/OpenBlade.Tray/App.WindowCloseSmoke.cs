#if OPENBLADE_DEVELOPER_HARNESS
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace OpenBlade.Tray;

public partial class App
{
    private static async Task RunWindowCloseSmokeAsync(string reportPath)
    {
        Window? lifetimeWindow = null;
        Window? window = null;
        WindowsWindowCloseHandler? handler = null;
        Exception? failure = null;
        try
        {
            _ = ReadResourceDesktopIdentity(requireInputDesktop: false);
            // Keep the dispatcher alive while the tested windows are destroyed.
            lifetimeWindow = new Window { Content = new Grid() };
            window = new Window { Content = new Grid() };
            AppWindow appWindow = window.AppWindow;
            nint handle = WindowNative.GetWindowHandle(window);
            int callbacks = 0;
            handler = new WindowsWindowCloseHandler(window, () =>
            {
                callbacks++;
                appWindow.Hide();
                return true;
            });
            bool duplicateRejected = false;
            try { using var duplicate = new WindowsWindowCloseHandler(window, () => false); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            if (!duplicateRejected || !WindowsWindowCloseHandler.IsAttached(handle))
            {
                throw new InvalidOperationException("A duplicate owner replaced the active close handler.");
            }
            for (int cycle = 0; cycle < 50; cycle++)
            {
                ShowOffscreenResourceWindow(window, requireInputDesktop: false);
                _ = WindowCloseSmokeNative.SendMessage(handle, 0x0010, 0, 0);
                await DrainDispatcherAsync(window.DispatcherQueue);
                AssertCloseWindowState(handle, appWindow, expectedVisible: false);
            }
            ShowOffscreenResourceWindow(window, requireInputDesktop: false);
            // The system-menu/title-bar close command reaches the same WM_CLOSE.
            _ = WindowCloseSmokeNative.SendMessage(handle, 0x0112, 0xF060, 0);
            await DrainDispatcherAsync(window.DispatcherQueue);
            AssertCloseWindowState(handle, appWindow, expectedVisible: false);
            if (callbacks != 51) { throw new InvalidOperationException("Close commands were not handled exactly once."); }

            handler.Dispose();
            handler.Dispose();
            if (WindowsWindowCloseHandler.IsAttached(handle)) { throw new InvalidOperationException("The retired handler remained attached."); }
            int replacementCallbacks = 0;
            handler = new WindowsWindowCloseHandler(window, () =>
            {
                replacementCallbacks++;
                appWindow.Hide();
                return true;
            });
            ShowOffscreenResourceWindow(window, requireInputDesktop: false);
            _ = WindowCloseSmokeNative.SendMessage(handle, 0x0010, 0, 0);
            AssertCloseWindowState(handle, appWindow, expectedVisible: false);
            if (callbacks != 51 || replacementCallbacks != 1) { throw new InvalidOperationException("A retired close handler received a new close request."); }
            handler.Dispose();

            handler = new WindowsWindowCloseHandler(window, () => throw new InvalidOperationException("Expected close-handler failure."));
            ShowOffscreenResourceWindow(window, requireInputDesktop: false);
            _ = WindowCloseSmokeNative.SendMessage(handle, 0x0010, 0, 0);
            AssertCloseWindowState(handle, appWindow, expectedVisible: true);
            handler.Dispose();

            // A permitted close must reach Windows, destroy the shell, and remove
            // the native callback even before the managed owner is disposed.
            handler = new WindowsWindowCloseHandler(window, () => false);
            _ = WindowCloseSmokeNative.SendMessage(handle, 0x0010, 0, 0);
            await DrainDispatcherAsync(lifetimeWindow.DispatcherQueue);
            AssertCloseWindowDestroyed(handle);
            window = null;
            handler.Dispose();

            // WinUI's explicit terminal Close must still destroy a window whose
            // user-close handler would normally request hiding.
            window = new Window { Content = new Grid() };
            handle = WindowNative.GetWindowHandle(window);
            handler = new WindowsWindowCloseHandler(window, () => true);
            window.Close();
            window = null;
            await DrainDispatcherAsync(lifetimeWindow.DispatcherQueue);
            AssertCloseWindowDestroyed(handle);
            handler.Dispose();
            handler = null;
            File.WriteAllText(reportPath, "Repeated close, system close, replacement, callback failure, permitted close and terminal cleanup: passed\n");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            Environment.ExitCode = failure is null ? 0 : 1;
            if (failure is not null) { File.WriteAllText(reportPath, failure.ToString()); }
            handler?.Dispose();
            window?.Close();
            lifetimeWindow?.Close();
        }
        Environment.Exit(failure is null ? 0 : 1);
    }

    private static void AssertCloseWindowState(nint handle, AppWindow appWindow, bool expectedVisible)
    {
        if (!WindowCloseSmokeNative.IsWindow(handle) || appWindow.IsVisible != expectedVisible
            || !WindowsWindowCloseHandler.IsAttached(handle))
        {
            throw new InvalidOperationException("A consumed close changed the shell lifetime or unexpected visibility.");
        }
    }

    private static void AssertCloseWindowDestroyed(nint handle)
    {
        if (WindowCloseSmokeNative.IsWindow(handle) || WindowsWindowCloseHandler.IsAttached(handle))
        {
            throw new InvalidOperationException("A terminal close retained its native window or callback.");
        }
    }

    private static class WindowCloseSmokeNative
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static extern nint SendMessage(nint window, uint message, nuint windowParameter, nint longParameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);
    }
}
#endif
