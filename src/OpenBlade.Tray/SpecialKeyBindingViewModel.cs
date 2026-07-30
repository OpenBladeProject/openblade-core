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
        new(SpecialKeyActionKind.Default, "Default"),
        new(SpecialKeyActionKind.Disabled, "Disabled"),
        new(SpecialKeyActionKind.KeyboardShortcut, "Keyboard shortcut"),
        new(SpecialKeyActionKind.MouseLeftClick, "Left click"),
        new(SpecialKeyActionKind.MouseRightClick, "Right click"),
        new(SpecialKeyActionKind.MouseMiddleClick, "Middle click"),
        new(SpecialKeyActionKind.MouseBack, "Mouse back"),
        new(SpecialKeyActionKind.MouseForward, "Mouse forward"),
        new(SpecialKeyActionKind.MouseDoubleClick, "Double click"),
        new(SpecialKeyActionKind.MouseScrollUp, "Scroll up"),
        new(SpecialKeyActionKind.MouseScrollDown, "Scroll down"),
        new(SpecialKeyActionKind.MediaPreviousTrack, "Previous track"),
        new(SpecialKeyActionKind.MediaPlayPause, "Play or pause"),
        new(SpecialKeyActionKind.MediaNextTrack, "Next track"),
        new(SpecialKeyActionKind.VolumeMute, "Mute volume"),
        new(SpecialKeyActionKind.VolumeDown, "Volume down"),
        new(SpecialKeyActionKind.VolumeUp, "Volume up"),
        new(SpecialKeyActionKind.MicrophoneMute, "Mute microphone"),
        new(SpecialKeyActionKind.LaunchApplication, "Launch an app"),
        new(SpecialKeyActionKind.OpenWebsite, "Open a website"),
        new(SpecialKeyActionKind.TypeText, "Type text"),
        new(SpecialKeyActionKind.CyclePowerMode, "Cycle OpenBlade power mode"),
        new(SpecialKeyActionKind.ToggleGamingMode, "Toggle Gaming mode"),
        new(SpecialKeyActionKind.CycleLightingEffect, "Cycle keyboard effect"),
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

    public SpecialKeyBindingViewModel BindingTarget => this;

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
            OnPropertyChanged(nameof(AccessLabel));
        }
    }

    public string AccessLabel =>
        IsEditable ? "Available" : "Captured only; input is not distinguishable";

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
        SpecialKeyActionKind.KeyboardShortcut => "Shortcut",
        SpecialKeyActionKind.LaunchApplication => "App or file path",
        SpecialKeyActionKind.OpenWebsite => "Website",
        SpecialKeyActionKind.TypeText => "Text",
        _ => string.Empty,
    };

    public string ValuePlaceholder => SelectedAction.Kind switch
    {
        SpecialKeyActionKind.KeyboardShortcut => "For example: Ctrl+Shift+M",
        SpecialKeyActionKind.LaunchApplication => @"C:\Path\To\App.exe",
        SpecialKeyActionKind.OpenWebsite => "https://example.com",
        SpecialKeyActionKind.TypeText => "Text to type",
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
}
