using System.ComponentModel;

namespace OpenBlade.Tray;

public enum TrayDashboardControlKind
{
    Performance,
    Fans,
    Battery,
    Lighting,
}

public enum TrayDashboardControlSize
{
    Compact,
    Wide,
}

public enum TrayDashboardStatusItemKind
{
    BatteryPower,
    Temperatures,
    Fans,
}

public sealed record TrayDashboardControlPreference(
    TrayDashboardControlKind Kind,
    bool IsVisible,
    TrayDashboardControlSize Size);

public sealed record TrayDashboardStatusItemPreference(
    TrayDashboardStatusItemKind Kind,
    bool IsVisible);

public sealed record TrayDashboardPreference(
    IReadOnlyList<TrayDashboardControlPreference>? Controls = null,
    TrayDashboardControlKind? ExpandedControl = null,
    IReadOnlyList<TrayDashboardStatusItemPreference>? StatusItems = null);

public sealed class TrayDashboardControlOption : INotifyPropertyChanged
{
    private bool isVisible;
    private bool isSizeEnabled = true;
    private TrayDashboardControlSize size;

    public TrayDashboardControlOption(
        TrayDashboardControlKind kind,
        string label,
        string description,
        bool isVisible,
        TrayDashboardControlSize size)
    {
        Kind = kind;
        Label = label;
        Description = description;
        this.isVisible = isVisible;
        this.size = size;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TrayDashboardControlKind Kind { get; }

    public string Label { get; }

    public string Description { get; }

    public IReadOnlyList<TrayDashboardControlSize> AvailableSizes { get; } =
        Enum.GetValues<TrayDashboardControlSize>();

    public bool IsSizeEnabled
    {
        get => isSizeEnabled;
        internal set
        {
            if (isSizeEnabled == value)
            {
                return;
            }

            isSizeEnabled = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSizeEnabled)));
        }
    }

    public bool IsVisible
    {
        get => isVisible;
        set
        {
            if (isVisible == value)
            {
                return;
            }

            isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public TrayDashboardControlSize Size
    {
        get => size;
        set
        {
            if (size == value)
            {
                return;
            }

            size = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
        }
    }
}

public sealed class TrayDashboardStatusItemOption : INotifyPropertyChanged
{
    private bool isVisible;

    public TrayDashboardStatusItemOption(
        TrayDashboardStatusItemKind kind,
        string label,
        bool isVisible)
    {
        Kind = kind;
        Label = label;
        this.isVisible = isVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TrayDashboardStatusItemKind Kind { get; }

    public string Label { get; }

    public bool IsVisible
    {
        get => isVisible;
        set
        {
            if (isVisible == value)
            {
                return;
            }

            isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }
}

public sealed record TrayDashboardExpandedControlOption(
    TrayDashboardControlKind? Kind,
    string Label);

public static class TrayDashboardPreferenceDefaults
{
    private sealed record ControlDefinition(
        TrayDashboardControlKind Kind,
        string Label,
        string Description,
        bool IsVisible,
        TrayDashboardControlSize Size);

    private static readonly ControlDefinition[] ControlDefinitions =
    [
        new(
            TrayDashboardControlKind.Performance,
            "Performance",
            "Choose the active power mode.",
            true,
            TrayDashboardControlSize.Compact),
        new(
            TrayDashboardControlKind.Fans,
            "Fans",
            "Choose the current fan mode.",
            true,
            TrayDashboardControlSize.Compact),
        new(
            TrayDashboardControlKind.Battery,
            "Battery",
            "Adjust the battery charge limit.",
            true,
            TrayDashboardControlSize.Compact),
        new(
            TrayDashboardControlKind.Lighting,
            "Lighting",
            "Choose the keyboard effect and brightness.",
            true,
            TrayDashboardControlSize.Compact),
    ];

    private static readonly TrayDashboardStatusItemKind[] StatusDefinitions =
        Enum.GetValues<TrayDashboardStatusItemKind>();

    public static TrayDashboardPreference Preference { get; } = new(
        ControlDefinitions
            .Select(definition => new TrayDashboardControlPreference(
                definition.Kind,
                definition.IsVisible,
                definition.Size))
            .ToArray(),
        TrayDashboardControlKind.Performance,
        StatusDefinitions
            .Select(kind => new TrayDashboardStatusItemPreference(kind, true))
            .ToArray());

    public static IReadOnlyList<TrayDashboardExpandedControlOption> ExpandedControls { get; } =
    [
        new(null, "None"),
        .. ControlDefinitions.Select(definition =>
            new TrayDashboardExpandedControlOption(definition.Kind, definition.Label)),
    ];

    public static TrayDashboardPreference Normalize(TrayDashboardPreference? preference)
    {
        if (preference is null)
        {
            return Preference;
        }

        IReadOnlyDictionary<TrayDashboardControlKind, ControlDefinition> definitions =
            ControlDefinitions.ToDictionary(definition => definition.Kind);
        var seenControls = new HashSet<TrayDashboardControlKind>();
        var controls = new List<TrayDashboardControlPreference>(ControlDefinitions.Length);
        foreach (TrayDashboardControlPreference control in preference.Controls ?? [])
        {
            if (!Enum.IsDefined(control.Kind)
                || !definitions.ContainsKey(control.Kind)
                || !seenControls.Add(control.Kind))
            {
                continue;
            }

            controls.Add(control with
            {
                Size = Enum.IsDefined(control.Size)
                    ? control.Size
                    : TrayDashboardControlSize.Compact,
            });
        }

        foreach (ControlDefinition definition in ControlDefinitions)
        {
            if (seenControls.Add(definition.Kind))
            {
                controls.Add(new TrayDashboardControlPreference(
                    definition.Kind,
                    false,
                    TrayDashboardControlSize.Compact));
            }
        }

        var seenStatusItems = new HashSet<TrayDashboardStatusItemKind>();
        var statusItems = new List<TrayDashboardStatusItemPreference>(
            StatusDefinitions.Length);
        foreach (TrayDashboardStatusItemPreference status in preference.StatusItems ?? [])
        {
            if (!Enum.IsDefined(status.Kind) || !seenStatusItems.Add(status.Kind))
            {
                continue;
            }

            statusItems.Add(status);
        }

        foreach (TrayDashboardStatusItemKind kind in StatusDefinitions)
        {
            if (seenStatusItems.Add(kind))
            {
                statusItems.Add(new TrayDashboardStatusItemPreference(kind, false));
            }
        }

        TrayDashboardControlKind? expanded = preference.ExpandedControl;
        if (expanded is { } selected
            && (!Enum.IsDefined(selected)
                || controls.All(control => control.Kind != selected || !control.IsVisible)))
        {
            expanded = null;
        }

        return new TrayDashboardPreference(controls, expanded, statusItems);
    }

    public static IReadOnlyList<TrayDashboardControlOption> CreateControlOptions(
        TrayDashboardPreference? preference)
    {
        TrayDashboardPreference normalized = Normalize(preference);
        IReadOnlyDictionary<TrayDashboardControlKind, ControlDefinition> definitions =
            ControlDefinitions.ToDictionary(definition => definition.Kind);
        return normalized.Controls!
            .Select(control =>
            {
                ControlDefinition definition = definitions[control.Kind];
                return new TrayDashboardControlOption(
                    control.Kind,
                    definition.Label,
                    definition.Description,
                    control.IsVisible,
                    control.Size);
            })
            .ToArray();
    }

    public static IReadOnlyList<TrayDashboardStatusItemOption> CreateStatusOptions(
        TrayDashboardPreference? preference)
    {
        TrayDashboardPreference normalized = Normalize(preference);
        return normalized.StatusItems!
            .Select(status => new TrayDashboardStatusItemOption(
                status.Kind,
                StatusLabel(status.Kind),
                status.IsVisible))
            .ToArray();
    }

    public static void UpdateSizeAvailability(
        IEnumerable<TrayDashboardControlOption> options,
        TrayDashboardControlKind? expandedControl)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (TrayDashboardControlOption option in options)
        {
            option.IsSizeEnabled = option.Kind != expandedControl;
        }
    }

    private static string StatusLabel(TrayDashboardStatusItemKind kind) => kind switch
    {
        TrayDashboardStatusItemKind.BatteryPower => "Battery power",
        TrayDashboardStatusItemKind.Temperatures => "Temperatures",
        TrayDashboardStatusItemKind.Fans => "Fans",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
