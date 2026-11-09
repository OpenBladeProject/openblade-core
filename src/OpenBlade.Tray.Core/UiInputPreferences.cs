namespace OpenBlade.Tray;

/// <summary>Input settings read independently of unrelated UI preferences.</summary>
public sealed record UiInputPreferences(
    SpecialKeyBindingPreference[]? SpecialKeyBindings = null,
    bool SnapTapEnabled = false,
    bool ThxAudioIntegrationEnabled = false,
    KeyboardInputBackend SnapTapBackend = KeyboardInputBackend.RazerDriver,
    KeyboardInputBackend FunctionRowBackend = KeyboardInputBackend.WindowsHook);
