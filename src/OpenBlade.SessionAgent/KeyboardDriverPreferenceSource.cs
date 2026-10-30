using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Windows SnapTap demand remains independent of the shared Razer owner.</summary>
internal sealed class WindowsSnapTapPreferenceSource : ISnapTapBackendPreferenceSource, IDisposable
{
    private readonly ISnapTapBackendPreferenceSource source;
    private int enabled;
    internal WindowsSnapTapPreferenceSource(ISnapTapBackendPreferenceSource source)
    {
        this.source = source;
        enabled = ReadEnabled() ? 1 : 0;
        source.SnapTapEnabledChanged += OnEnabled;
        source.SnapTapBackendChanged += OnBackend;
    }
    public bool SnapTapEnabled => Volatile.Read(ref enabled) != 0;
    public KeyboardInputBackend SnapTapBackend => KeyboardInputBackend.WindowsHook;
    public event Action<bool>? SnapTapEnabledChanged;
    public event Action<KeyboardInputBackend>? SnapTapBackendChanged { add { } remove { } }
    private bool ReadEnabled() => source.SnapTapEnabled && source.SnapTapBackend == KeyboardInputBackend.WindowsHook;
    private void OnEnabled(bool _) => Refresh();
    private void OnBackend(KeyboardInputBackend _) => Refresh();
    private void Refresh()
    {
        bool next = ReadEnabled();
        if (Interlocked.Exchange(ref enabled, next ? 1 : 0) != (next ? 1 : 0))
        {
            SnapTapEnabledChanged?.Invoke(next);
        }
    }
    public void Dispose()
    {
        source.SnapTapEnabledChanged -= OnEnabled;
        source.SnapTapBackendChanged -= OnBackend;
    }
}

/// <summary>A changed union stops the entire driver owner before rearming it.</summary>
internal sealed class RazerKeyboardDriverPreferenceSource : ISnapTapBackendPreferenceSource, IDisposable
{
    private readonly SpecialKeyBindingSource? source;
    private int features;
    internal RazerKeyboardDriverPreferenceSource(SpecialKeyBindingSource source)
    {
        this.source = source;
        features = (int)SelectFeatures(source.SnapTapEnabled, source.SnapTapBackend, source.FunctionRowBackend);
        source.PreferencesObserved += OnObserved;
    }
    internal RazerKeyboardDriverPreferenceSource() { }
    internal KeyboardDriverFeatures RequestedFeatures => (KeyboardDriverFeatures)Volatile.Read(ref features);
    internal bool RequestsFunctionRow => (RequestedFeatures & KeyboardDriverFeatures.FunctionRow) != 0;
    public bool SnapTapEnabled => RequestedFeatures != KeyboardDriverFeatures.None;
    public KeyboardInputBackend SnapTapBackend => KeyboardInputBackend.RazerDriver;
    public event Action<bool>? SnapTapEnabledChanged;
    public event Action<KeyboardInputBackend>? SnapTapBackendChanged { add { } remove { } }

    internal static KeyboardDriverFeatures SelectFeatures(bool snapTapEnabled,
        KeyboardInputBackend snapTapBackend, KeyboardInputBackend functionRowBackend) =>
        (snapTapEnabled && snapTapBackend == KeyboardInputBackend.RazerDriver ? KeyboardDriverFeatures.SnapTap : 0)
        | (functionRowBackend == KeyboardInputBackend.RazerDriver ? KeyboardDriverFeatures.FunctionRow : 0);

    private void OnObserved(UiPreferences? preferences)
    {
        if (preferences is not null)
        {
            SetFeatures(SelectFeatures(preferences.SnapTapEnabled,
                preferences.SnapTapBackend, preferences.FunctionRowBackend));
        }
    }

    internal void SetFeatures(KeyboardDriverFeatures value)
    {
        if ((value & ~(KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (Interlocked.Exchange(ref features, (int)value) == (int)value) { return; }
        // The coordinator records every false generation even if its capacity-one
        // notification channel coalesces these two events.
        SnapTapEnabledChanged?.Invoke(false);
        if (value != KeyboardDriverFeatures.None) { SnapTapEnabledChanged?.Invoke(true); }
    }

    public void Dispose()
    {
        if (source is not null) { source.PreferencesObserved -= OnObserved; }
    }
}
