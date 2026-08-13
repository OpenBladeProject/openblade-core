using System.ComponentModel;

namespace OpenBlade.Tray;

public enum TrayDashboardControlKind
{
    Performance,
    Fans,
    Battery,
    Lighting,
    BatteryProtection,
    FunctionRow,
    GamingMode,
    GpuTuning,
}

public enum TrayDashboardStatusItemKind
{
    BatteryPower,
    Temperatures,
    Fans,
}

public enum TrayDashboardSliderKind
{
    ChargeLimit,
    LightingBrightness,
    GpuCoreClock,
    GpuMemoryClock,
}

public sealed record TrayDashboardControlPreference(
    TrayDashboardControlKind Kind,
    bool IsVisible);

public sealed record TrayDashboardStatusItemPreference(
    TrayDashboardStatusItemKind Kind,
    bool IsVisible);

public sealed record TrayDashboardSliderPreference(
    TrayDashboardSliderKind Kind,
    bool IsVisible);

public sealed record TrayDashboardPreference(
    IReadOnlyList<TrayDashboardControlPreference>? Controls = null,
    IReadOnlyList<TrayDashboardStatusItemPreference>? StatusItems = null,
    IReadOnlyList<TrayDashboardSliderPreference>? Sliders = null,
    int SchemaVersion = 0);

public sealed class TrayDashboardControlOption : INotifyPropertyChanged
{
    private bool isVisible;

    public TrayDashboardControlOption(
        TrayDashboardControlKind kind,
        string label,
        string description,
        bool isVisible)
    {
        Kind = kind;
        Label = label;
        Description = description;
        this.isVisible = isVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TrayDashboardControlKind Kind { get; }

    public string Label { get; }

    public string Description { get; }

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

public sealed class TrayDashboardSliderOption : INotifyPropertyChanged
{
    private bool isVisible;

    public TrayDashboardSliderOption(
        TrayDashboardSliderKind kind,
        string label,
        bool isVisible)
    {
        Kind = kind;
        Label = label;
        this.isVisible = isVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TrayDashboardSliderKind Kind { get; }

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

public static class TrayDashboardPreferenceDefaults
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumVisibleSliders = 2;

    private sealed record ControlDefinition(
        TrayDashboardControlKind Kind,
        string Label,
        string Description,
        bool IsVisible);

    private static readonly ControlDefinition[] ControlDefinitions =
    [
        new(
            TrayDashboardControlKind.Performance,
            "Performance",
            "Choose the active power mode.",
            true),
        new(
            TrayDashboardControlKind.Fans,
            "Fans",
            "Choose the current fan mode.",
            true),
        new(
            TrayDashboardControlKind.Lighting,
            "Lighting",
            "Choose the keyboard effect and brightness.",
            true),
        new(
            TrayDashboardControlKind.BatteryProtection,
            "Battery protection",
            "Turn the configured battery charge limit on or off.",
            true),
        new(
            TrayDashboardControlKind.FunctionRow,
            "Function row",
            "Choose function keys or media keys as the primary action.",
            true),
        new(
            TrayDashboardControlKind.GamingMode,
            "Gaming mode",
            "Turn the laptop gaming mode on or off.",
            true),
        new(
            TrayDashboardControlKind.GpuTuning,
            "GPU tuning",
            "Turn GPU clock tuning on or off.",
            false),
    ];

    private static readonly TrayDashboardStatusItemKind[] StatusDefinitions =
        Enum.GetValues<TrayDashboardStatusItemKind>();

    private static readonly (TrayDashboardSliderKind Kind, string Label, bool IsVisible)[]
        SliderDefinitions =
        [
            (TrayDashboardSliderKind.LightingBrightness, "Lighting brightness", true),
            (TrayDashboardSliderKind.ChargeLimit, "Charge limit", true),
            (TrayDashboardSliderKind.GpuCoreClock, "GPU core clock", false),
            (TrayDashboardSliderKind.GpuMemoryClock, "GPU memory clock", false),
        ];

    public static TrayDashboardPreference Preference { get; } = new(
        ControlDefinitions
            .Select(definition => new TrayDashboardControlPreference(
                definition.Kind,
                definition.IsVisible))
            .ToArray(),
        StatusItems: StatusDefinitions
            .Select(kind => new TrayDashboardStatusItemPreference(kind, true))
            .ToArray(),
        Sliders: SliderDefinitions
            .Select(definition => new TrayDashboardSliderPreference(
                definition.Kind,
                definition.IsVisible))
            .ToArray(),
        SchemaVersion: CurrentSchemaVersion);

    public static TrayDashboardPreference Normalize(TrayDashboardPreference? preference)
    {
        if (preference is null)
        {
            return Preference;
        }
        if (preference.SchemaVersion < 0
            || preference.SchemaVersion > CurrentSchemaVersion)
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

            controls.Add(control);
        }

        foreach (ControlDefinition definition in ControlDefinitions)
        {
            if (seenControls.Add(definition.Kind))
            {
                controls.Add(new TrayDashboardControlPreference(
                    definition.Kind,
                    false));
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

        IReadOnlyDictionary<TrayDashboardSliderKind, (string Label, bool IsVisible)>
            sliderDefinitions = SliderDefinitions.ToDictionary(
                definition => definition.Kind,
                definition => (definition.Label, definition.IsVisible));
        var seenSliders = new HashSet<TrayDashboardSliderKind>();
        var sliders = new List<TrayDashboardSliderPreference>(SliderDefinitions.Length);
        foreach (TrayDashboardSliderPreference slider in preference.Sliders ?? [])
        {
            if (!Enum.IsDefined(slider.Kind)
                || !sliderDefinitions.ContainsKey(slider.Kind)
                || !seenSliders.Add(slider.Kind))
            {
                continue;
            }

            sliders.Add(slider);
        }

        bool migratingLegacyControls = preference.Sliders is null;
        foreach ((TrayDashboardSliderKind kind, _, bool defaultVisibility) in
                 SliderDefinitions)
        {
            if (!seenSliders.Add(kind))
            {
                continue;
            }

            bool isVisible = migratingLegacyControls
                ? kind switch
                {
                    TrayDashboardSliderKind.ChargeLimit =>
                        LegacyControlVisibility(
                            preference,
                            TrayDashboardControlKind.Battery,
                            defaultVisibility),
                    TrayDashboardSliderKind.LightingBrightness =>
                        LegacyControlVisibility(
                            preference,
                            TrayDashboardControlKind.Lighting,
                            defaultVisibility),
                    _ => defaultVisibility,
                }
                : false;
            sliders.Add(new TrayDashboardSliderPreference(kind, isVisible));
        }

        int visibleSliderCount = 0;
        for (int index = 0; index < sliders.Count; index++)
        {
            TrayDashboardSliderPreference slider = sliders[index];
            if (!slider.IsVisible)
            {
                continue;
            }

            visibleSliderCount++;
            if (visibleSliderCount > MaximumVisibleSliders)
            {
                sliders[index] = slider with { IsVisible = false };
            }
        }

        return new TrayDashboardPreference(
            Controls: controls,
            StatusItems: statusItems,
            Sliders: sliders,
            SchemaVersion: CurrentSchemaVersion);
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
                    control.IsVisible);
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

    public static IReadOnlyList<TrayDashboardSliderOption> CreateSliderOptions(
        TrayDashboardPreference? preference)
    {
        TrayDashboardPreference normalized = Normalize(preference);
        IReadOnlyDictionary<TrayDashboardSliderKind, string> labels =
            SliderDefinitions.ToDictionary(
                definition => definition.Kind,
                definition => definition.Label);
        return normalized.Sliders!
            .Select(slider => new TrayDashboardSliderOption(
                slider.Kind,
                labels[slider.Kind],
                slider.IsVisible))
            .ToArray();
    }

    private static bool LegacyControlVisibility(
        TrayDashboardPreference preference,
        TrayDashboardControlKind kind,
        bool defaultVisibility)
    {
        if (preference.Controls is null)
        {
            return defaultVisibility;
        }

        return preference.Controls
            .FirstOrDefault(control => control.Kind == kind)?
            .IsVisible
            ?? false;
    }

    private static string StatusLabel(TrayDashboardStatusItemKind kind) => kind switch
    {
        TrayDashboardStatusItemKind.BatteryPower => "Battery power",
        TrayDashboardStatusItemKind.Temperatures => "Temperatures",
        TrayDashboardStatusItemKind.Fans => "Fans",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
