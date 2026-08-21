using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace OpenBlade.Tray;

public sealed record SpecialKeyActionOption(
    SpecialKeyActionKind Kind,
    string Label);

public sealed class SpecialKeyBindingViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<SpecialKeyActionOption> availableActions;
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
        availableActions = CreateActionOptions();
        selectedAction = availableActions.Single(option => option.Kind == normalized.Kind);
        value = normalized.Value ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SpecialKeySlot Slot { get; }

    public IReadOnlyList<SpecialKeyActionOption> AvailableActions => availableActions;

    public string SlotLabel => Slot switch
    {
        SpecialKeySlot.M1 => LocalizedResources.Get("SpecialKey_Slot_M1", "M1"),
        SpecialKeySlot.M2 => LocalizedResources.Get("SpecialKey_Slot_M2", "M2"),
        SpecialKeySlot.M3 => LocalizedResources.Get("SpecialKey_Slot_M3", "M3"),
        SpecialKeySlot.M4 => LocalizedResources.Get("SpecialKey_Slot_M4", "M4"),
        SpecialKeySlot.M5 => LocalizedResources.Get("SpecialKey_Slot_M5", "M5"),
        _ => string.Empty,
    };

    public string ActionAccessibleName => LocalizedResources.Format(
        "SpecialKey_Action_AccessibleName",
        "Action for {0}",
        SlotLabel);

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
        SpecialKeyActionKind.LaunchApplication => LocalizedResources.Get(
            "SpecialKey_ValuePlaceholder_AppPath",
            "C:\\Path\\To\\App.exe"),
        SpecialKeyActionKind.OpenWebsite => LocalizedResources.Get(
            "SpecialKey_ValuePlaceholder_Website",
            "https://example.com"),
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

    private static IReadOnlyList<SpecialKeyActionOption> CreateActionOptions() =>
    [
        new(SpecialKeyActionKind.Default, LocalizedResources.Get("SpecialKey_Action_Default", "Default")),
        new(SpecialKeyActionKind.Disabled, LocalizedResources.Get("SpecialKey_Action_Disabled", "Disabled")),
        new(SpecialKeyActionKind.KeyboardShortcut, LocalizedResources.Get("SpecialKey_Action_KeyboardShortcut", "Keyboard shortcut")),
        new(SpecialKeyActionKind.MouseLeftClick, LocalizedResources.Get("SpecialKey_Action_LeftClick", "Left click")),
        new(SpecialKeyActionKind.MouseRightClick, LocalizedResources.Get("SpecialKey_Action_RightClick", "Right click")),
        new(SpecialKeyActionKind.MouseMiddleClick, LocalizedResources.Get("SpecialKey_Action_MiddleClick", "Middle click")),
        new(SpecialKeyActionKind.MouseBack, LocalizedResources.Get("SpecialKey_Action_MouseBack", "Mouse back")),
        new(SpecialKeyActionKind.MouseForward, LocalizedResources.Get("SpecialKey_Action_MouseForward", "Mouse forward")),
        new(SpecialKeyActionKind.MouseDoubleClick, LocalizedResources.Get("SpecialKey_Action_DoubleClick", "Double click")),
        new(SpecialKeyActionKind.MouseScrollUp, LocalizedResources.Get("SpecialKey_Action_ScrollUp", "Scroll up")),
        new(SpecialKeyActionKind.MouseScrollDown, LocalizedResources.Get("SpecialKey_Action_ScrollDown", "Scroll down")),
        new(SpecialKeyActionKind.MediaPreviousTrack, LocalizedResources.Get("SpecialKey_Action_PreviousTrack", "Previous track")),
        new(SpecialKeyActionKind.MediaPlayPause, LocalizedResources.Get("SpecialKey_Action_PlayPause", "Play or pause")),
        new(SpecialKeyActionKind.MediaNextTrack, LocalizedResources.Get("SpecialKey_Action_NextTrack", "Next track")),
        new(SpecialKeyActionKind.VolumeMute, LocalizedResources.Get("SpecialKey_Action_MuteVolume", "Mute volume")),
        new(SpecialKeyActionKind.VolumeDown, LocalizedResources.Get("SpecialKey_Action_VolumeDown", "Volume down")),
        new(SpecialKeyActionKind.VolumeUp, LocalizedResources.Get("SpecialKey_Action_VolumeUp", "Volume up")),
        new(SpecialKeyActionKind.MicrophoneMute, LocalizedResources.Get("SpecialKey_Action_MuteMicrophone", "Mute microphone")),
        new(SpecialKeyActionKind.LaunchApplication, LocalizedResources.Get("SpecialKey_Action_LaunchApplication", "Launch an app")),
        new(SpecialKeyActionKind.OpenWebsite, LocalizedResources.Get("SpecialKey_Action_OpenWebsite", "Open a website")),
        new(SpecialKeyActionKind.TypeText, LocalizedResources.Get("SpecialKey_Action_TypeText", "Type text")),
        new(SpecialKeyActionKind.CyclePowerMode, LocalizedResources.Get("SpecialKey_Action_CyclePowerMode", "Cycle OpenBlade power mode")),
        new(SpecialKeyActionKind.ToggleGamingMode, LocalizedResources.Get("SpecialKey_Action_ToggleGamingMode", "Toggle Gaming mode")),
        new(SpecialKeyActionKind.CycleLightingEffect, LocalizedResources.Get("SpecialKey_Action_CycleLightingEffect", "Cycle keyboard effect")),
    ];

}
