using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace OpenBlade.Tray;

public sealed class WindowsShellThemeSource : ITrayIconThemeSource, IDisposable
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeValueName = "SystemUsesLightTheme";
    private readonly DispatcherQueue dispatcherQueue;
    private readonly UISettings uiSettings = new();
    private bool useLightTrayIconGlyphs;
    private int disposed;

    public WindowsShellThemeSource(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);

        this.dispatcherQueue = dispatcherQueue;
        useLightTrayIconGlyphs = ReadUseLightTrayIconGlyphs();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        uiSettings.ColorValuesChanged += OnColorValuesChanged;
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
        uiSettings.ColorValuesChanged -= OnColorValuesChanged;
    }

    // Windows theme switches also arrive through UISettings, independently of
    // the legacy preference event. Both callbacks may run off the UI thread.
    private void OnColorValuesChanged(UISettings sender, object args) => QueueThemeRefresh();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) =>
        QueueThemeRefresh();

    private void QueueThemeRefresh()
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
