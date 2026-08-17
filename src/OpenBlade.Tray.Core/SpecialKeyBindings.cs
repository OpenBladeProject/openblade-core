namespace OpenBlade.Tray;

public enum SpecialKeySlot
{
    M1,
    M2,
    M3,
    M4,
    M5,
}

public enum SpecialKeyActionKind
{
    Default,
    Disabled,
    KeyboardShortcut,
    MouseLeftClick,
    MouseRightClick,
    MouseMiddleClick,
    MouseBack,
    MouseForward,
    MouseDoubleClick,
    MouseScrollUp,
    MouseScrollDown,
    MediaPreviousTrack,
    MediaPlayPause,
    MediaNextTrack,
    VolumeMute,
    VolumeDown,
    VolumeUp,
    MicrophoneMute,
    LaunchApplication,
    OpenWebsite,
    TypeText,
    CyclePowerMode,
    ToggleGamingMode,
    CycleLightingEffect,
}

public sealed record SpecialKeyAction(
    SpecialKeyActionKind Kind,
    string? Value = null);

public sealed record SpecialKeyBindingPreference(
    SpecialKeySlot Slot,
    SpecialKeyAction Action);

public static class SpecialKeyBindingDefaults
{
    public const int MaximumTextLength = 250;
    public const int MaximumTargetLength = 2048;

    public static IReadOnlyList<SpecialKeyBindingPreference> Bindings { get; } =
        Enum.GetValues<SpecialKeySlot>()
            .Select(slot => new SpecialKeyBindingPreference(
                slot,
                new SpecialKeyAction(SpecialKeyActionKind.Default)))
            .ToArray();

    public static IReadOnlyList<SpecialKeyBindingPreference> Normalize(
        IReadOnlyList<SpecialKeyBindingPreference>? bindings)
    {
        var bySlot = new Dictionary<SpecialKeySlot, SpecialKeyAction>();
        if (bindings is not null)
        {
            foreach (SpecialKeyBindingPreference binding in bindings)
            {
                if (Enum.IsDefined(binding.Slot)
                    && !bySlot.ContainsKey(binding.Slot))
                {
                    bySlot[binding.Slot] = Normalize(binding.Action);
                }
            }
        }

        return Enum.GetValues<SpecialKeySlot>()
            .Select(slot => new SpecialKeyBindingPreference(
                slot,
                bySlot.GetValueOrDefault(
                    slot,
                    new SpecialKeyAction(SpecialKeyActionKind.Default))))
            .ToArray();
    }

    public static SpecialKeyAction Normalize(SpecialKeyAction? action)
    {
        if (action is null || !Enum.IsDefined(action.Kind))
        {
            return new SpecialKeyAction(SpecialKeyActionKind.Default);
        }

        string? value = string.IsNullOrWhiteSpace(action.Value)
            ? null
            : action.Value.Trim();
        int maximumLength = action.Kind == SpecialKeyActionKind.TypeText
            ? MaximumTextLength
            : MaximumTargetLength;
        if (value?.Length > maximumLength)
        {
            value = value[..maximumLength];
        }

        return new SpecialKeyAction(action.Kind, value);
    }

    public static bool RequiresValue(SpecialKeyActionKind kind) => kind is
        SpecialKeyActionKind.KeyboardShortcut
        or SpecialKeyActionKind.LaunchApplication
        or SpecialKeyActionKind.OpenWebsite
        or SpecialKeyActionKind.TypeText;

    public static bool OverridesDefaultBehavior(SpecialKeyAction action)
    {
        SpecialKeyAction normalized = Normalize(action);
        return normalized.Kind != SpecialKeyActionKind.Default
            && string.IsNullOrEmpty(GetValidationMessage(normalized));
    }

    public static string GetValidationMessage(SpecialKeyAction action)
    {
        SpecialKeyAction normalized = Normalize(action);
        if (RequiresValue(normalized.Kind)
            && string.IsNullOrWhiteSpace(normalized.Value))
        {
            return TrayText.Get("Enter a value for this action.");
        }

        if (normalized.Kind == SpecialKeyActionKind.KeyboardShortcut
            && !SpecialKeyShortcutParser.TryParse(
                normalized.Value,
                out string shortcutValidationMessage))
        {
            return shortcutValidationMessage;
        }

        if (normalized.Kind == SpecialKeyActionKind.OpenWebsite
            && (!Uri.TryCreate(normalized.Value, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")))
        {
            return TrayText.Get("Enter a complete http or https address.");
        }

        return string.Empty;
    }
}
