using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace OpenBlade.Tray;

public sealed class WindowsShellThemeSource : ITrayIconThemeSource, IDisposable
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeValueName = "SystemUsesLightTheme";
    private readonly DispatcherQueue dispatcherQueue;
    private bool useLightTrayIconGlyphs;
    private int disposed;

    public WindowsShellThemeSource(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);

        this.dispatcherQueue = dispatcherQueue;
        useLightTrayIconGlyphs = ReadUseLightTrayIconGlyphs();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler? TrayIconThemeChanged;

    public bool UseLightTrayIconGlyphs => useLightTrayIconGlyphs;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        dispatcherQueue.TryEnqueue(RefreshTheme);
    }

    private void RefreshTheme()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        bool next = ReadUseLightTrayIconGlyphs();
        if (next == useLightTrayIconGlyphs)
        {
            return;
        }

        useLightTrayIconGlyphs = next;
        TrayIconThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool ReadUseLightTrayIconGlyphs() =>
        ShellThemeSelectionPolicy.ShouldUseLightTrayIconGlyphs(
            ReadSystemUsesLightTheme());

    private static int? ReadSystemUsesLightTheme()
        => WindowsRegistryReader.ReadCurrentUserDword(
            PersonalizeRegistryPath,
            SystemUsesLightThemeValueName);
}
