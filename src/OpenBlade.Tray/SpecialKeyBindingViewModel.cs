using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace OpenBlade.Tray;

public sealed record SpecialKeyActionOption(
    SpecialKeyActionKind Kind,
    string Label);

public sealed class SpecialKeyBindingViewModel : INotifyPropertyChanged
{
    public static IReadOnlyList<SpecialKeyActionOption> ActionOptions { get; } =
    [
        Option(SpecialKeyActionKind.Default, "SpecialKey_Action_Default", "Default"),
        Option(SpecialKeyActionKind.Disabled, "SpecialKey_Action_Disabled", "Disabled"),
        Option(SpecialKeyActionKind.KeyboardShortcut, "SpecialKey_Action_KeyboardShortcut", "Keyboard shortcut"),
        Option(SpecialKeyActionKind.MouseLeftClick, "SpecialKey_Action_LeftClick", "Left click"),
        Option(SpecialKeyActionKind.MouseRightClick, "SpecialKey_Action_RightClick", "Right click"),
        Option(SpecialKeyActionKind.MouseMiddleClick, "SpecialKey_Action_MiddleClick", "Middle click"),
        Option(SpecialKeyActionKind.MouseBack, "SpecialKey_Action_MouseBack", "Mouse back"),
        Option(SpecialKeyActionKind.MouseForward, "SpecialKey_Action_MouseForward", "Mouse forward"),
        Option(SpecialKeyActionKind.MouseDoubleClick, "SpecialKey_Action_DoubleClick", "Double click"),
        Option(SpecialKeyActionKind.MouseScrollUp, "SpecialKey_Action_ScrollUp", "Scroll up"),
        Option(SpecialKeyActionKind.MouseScrollDown, "SpecialKey_Action_ScrollDown", "Scroll down"),
        Option(SpecialKeyActionKind.MediaPreviousTrack, "SpecialKey_Action_PreviousTrack", "Previous track"),
        Option(SpecialKeyActionKind.MediaPlayPause, "SpecialKey_Action_PlayPause", "Play or pause"),
        Option(SpecialKeyActionKind.MediaNextTrack, "SpecialKey_Action_NextTrack", "Next track"),
        Option(SpecialKeyActionKind.VolumeMute, "SpecialKey_Action_MuteVolume", "Mute volume"),
        Option(SpecialKeyActionKind.VolumeDown, "SpecialKey_Action_VolumeDown", "Volume down"),
        Option(SpecialKeyActionKind.VolumeUp, "SpecialKey_Action_VolumeUp", "Volume up"),
        Option(SpecialKeyActionKind.MicrophoneMute, "SpecialKey_Action_MuteMicrophone", "Mute microphone"),
        Option(SpecialKeyActionKind.LaunchApplication, "SpecialKey_Action_LaunchApplication", "Launch an app"),
        Option(SpecialKeyActionKind.OpenWebsite, "SpecialKey_Action_OpenWebsite", "Open a website"),
        Option(SpecialKeyActionKind.TypeText, "SpecialKey_Action_TypeText", "Type text"),
        Option(SpecialKeyActionKind.CyclePowerMode, "SpecialKey_Action_CyclePowerMode", "Cycle OpenBlade power mode"),
        Option(SpecialKeyActionKind.ToggleGamingMode, "SpecialKey_Action_ToggleGamingMode", "Toggle Gaming mode"),
        Option(SpecialKeyActionKind.CycleLightingEffect, "SpecialKey_Action_CycleLightingEffect", "Cycle keyboard effect"),
    ];

    private SpecialKeyActionOption selectedAction;
    private string value;
    private bool isEditable;

    public SpecialKeyBindingViewModel(
        SpecialKeyBindingPreference preference,
        bool isEditable = true)
    {
        ArgumentNullException.ThrowIfNull(preference);
        Slot = preference.Slot;
        this.isEditable = isEditable;
        SpecialKeyAction normalized = SpecialKeyBindingDefaults.Normalize(preference.Action);
        selectedAction = ActionOptions.Single(option => option.Kind == normalized.Kind);
        value = normalized.Value ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SpecialKeySlot Slot { get; }

    public IReadOnlyList<SpecialKeyActionOption> AvailableActions => ActionOptions;

    public string SlotLabel => Slot.ToString();

    public bool IsEditable
    {
        get => isEditable;
        private set
        {
            if (isEditable == value)
            {
                return;
            }
            isEditable = value;
            OnPropertyChanged();
        }
    }

    public void SetEditable(bool value) => IsEditable = value;

    public SpecialKeyActionOption SelectedAction
    {
        get => selectedAction;
        set
        {
            if (value is null || value == selectedAction)
            {
                return;
            }

            selectedAction = value;
            if (!string.IsNullOrEmpty(this.value))
            {
                this.value = string.Empty;
                OnPropertyChanged(nameof(Value));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueEditorVisibility));
            OnPropertyChanged(nameof(ValueLabel));
            OnPropertyChanged(nameof(ValuePlaceholder));
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(ValidationMessageVisibility));
        }
    }

    public string Value
    {
        get => value;
        set
        {
            string normalized = value ?? string.Empty;
            int maximumLength = SelectedAction.Kind == SpecialKeyActionKind.TypeText
                ? SpecialKeyBindingDefaults.MaximumTextLength
                : SpecialKeyBindingDefaults.MaximumTargetLength;
            if (normalized.Length > maximumLength)
            {
                normalized = normalized[..maximumLength];
            }
            if (this.value == normalized)
            {
                return;
            }

            this.value = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(ValidationMessageVisibility));
        }
    }

    public Visibility ValueEditorVisibility =>
        SpecialKeyBindingDefaults.RequiresValue(SelectedAction.Kind)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string ValueLabel => SelectedAction.Kind switch
    {
        SpecialKeyActionKind.KeyboardShortcut => LocalizedResources.Get("SpecialKey_ValueLabel_Shortcut", "Shortcut"),
        SpecialKeyActionKind.LaunchApplication => LocalizedResources.Get("SpecialKey_ValueLabel_AppPath", "App or file path"),
        SpecialKeyActionKind.OpenWebsite => LocalizedResources.Get("SpecialKey_ValueLabel_Website", "Website"),
        SpecialKeyActionKind.TypeText => LocalizedResources.Get("SpecialKey_ValueLabel_Text", "Text"),
        _ => string.Empty,
    };

    public string ValuePlaceholder => SelectedAction.Kind switch
    {
        SpecialKeyActionKind.KeyboardShortcut => LocalizedResources.Get("SpecialKey_ValuePlaceholder_Shortcut", "For example: Ctrl+Shift+M"),
        SpecialKeyActionKind.LaunchApplication => @"C:\Path\To\App.exe",
        SpecialKeyActionKind.OpenWebsite => "https://example.com",
        SpecialKeyActionKind.TypeText => LocalizedResources.Get("SpecialKey_ValuePlaceholder_Text", "Text to type"),
        _ => string.Empty,
    };

    public string ValidationMessage => SpecialKeyBindingDefaults.GetValidationMessage(
        new SpecialKeyAction(SelectedAction.Kind, Value));

    public Visibility ValidationMessageVisibility =>
        string.IsNullOrEmpty(ValidationMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public SpecialKeyBindingPreference ToPreference() => new(
        Slot,
        SpecialKeyBindingDefaults.Normalize(new SpecialKeyAction(
            SelectedAction.Kind,
            Value)));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static SpecialKeyActionOption Option(
        SpecialKeyActionKind kind,
        string resourceKey,
        string englishLabel) =>
        new(kind, LocalizedResources.Get(resourceKey, englishLabel));

}
