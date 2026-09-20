using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Tray;

/// <summary>Blocks snapshot HWND input without disabling the rendered controls.</summary>
internal static class LocalizationSnapshotWindowInput
{
    private const int ExtendedStyleIndex = -20;
    private const long NoActivateStyle = 0x08000000;
    private const uint NonClientHitTest = 0x0084;
    private const uint MouseActivate = 0x0021;
    private const uint NonClientDestroy = 0x0082;
    private const uint SubclassId = 0x4F425349; // OBSI
    private static readonly SubclassProcedure Callback = ProcessMessage;

    // Call on the owning UI thread before first show. The guard lasts until destroy.
    internal static void Apply(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            throw new ArgumentException("A snapshot window handle is required.", nameof(window));
        }
        if (!SetWindowSubclass(window, Callback, new UIntPtr(SubclassId), UIntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Snapshot input guard installation failed.");
        }
        long styles = GetWindowLongPtr(window, ExtendedStyleIndex).ToInt64();
        SetLastError(0);
        IntPtr previous = SetWindowLongPtr(window, ExtendedStyleIndex, new IntPtr(styles | NoActivateStyle));
        int error = Marshal.GetLastWin32Error();
        if (previous == IntPtr.Zero && error != 0)
        {
            _ = RemoveWindowSubclass(window, Callback, new UIntPtr(SubclassId));
            throw new Win32Exception(error, "Snapshot activation isolation failed.");
        }
    }

    private static IntPtr ProcessMessage(
        IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == NonClientHitTest)
        {
            return IntPtr.Zero; // HTNOWHERE: no caption, resize or client target.
        }
        if (message == MouseActivate)
        {
            return new IntPtr(4); // MA_NOACTIVATEANDEAT: swallow the activating click.
        }
        if (message == NonClientDestroy)
        {
            _ = RemoveWindowSubclass(window, Callback, subclassId);
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window, SubclassProcedure callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr window, SubclassProcedure callback, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint error);
}
