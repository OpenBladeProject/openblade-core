using System.ComponentModel;

namespace OpenBlade.Tray;

public enum DashboardCardKind
{
    DeviceStatus,
    PowerMode,
    Fans,
    Battery,
    KeyboardBehavior,
    StartupAnimation,
    Lighting,
    Display,
}

public sealed record DashboardCardPreference(
    DashboardCardKind Kind,
    bool IsPinned);

public sealed class DashboardCardOption : INotifyPropertyChanged
{
    private bool isPinned;

    public DashboardCardOption(
        DashboardCardKind kind,
        string label,
        string description,
        bool isPinned)
    {
        Kind = kind;
        Label = label;
        Description = description;
        this.isPinned = isPinned;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DashboardCardKind Kind { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (isPinned == value)
            {
                return;
            }

            isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }
}

public static class DashboardCardPreferenceDefaults
{
    private sealed record CardDefinition(
        DashboardCardKind Kind,
        string Label,
        string Description,
        bool IsPinnedByDefault);

    private static readonly CardDefinition[] Definitions =
    [
        new(
            DashboardCardKind.DeviceStatus,
            "Device status and live readings",
            "Connection, active settings, and the live readings you selected.",
            true),
        new(
            DashboardCardKind.PowerMode,
            "Power mode",
            "Choose the active power mode and custom CPU or GPU levels.",
            false),
        new(
            DashboardCardKind.Fans,
            "Fans",
            "Choose automatic, fixed, maximum, or manual fan control.",
            false),
        new(
            DashboardCardKind.Battery,
            "Battery",
            "Manage battery protection, charge limit, and temporary full charge.",
            false),
        new(
            DashboardCardKind.KeyboardBehavior,
            "Keyboard behavior",
            "Set function-key behavior and gaming mode.",
            false),
        new(
            DashboardCardKind.StartupAnimation,
            "Startup animation",
            "Choose whether the keyboard animation plays during startup.",
            false),
        new(
            DashboardCardKind.Lighting,
            "Lighting",
            "Control keyboard effects, brightness, and laptop logo lighting.",
            false),
        new(
            DashboardCardKind.Display,
            "Display",
            "Control refresh rate, HDR, and the color profile.",
            false),
    ];

    public static IReadOnlyList<DashboardCardPreference> Cards { get; } =
        Definitions
            .Select(definition => new DashboardCardPreference(
                definition.Kind,
                definition.IsPinnedByDefault))
            .ToArray();

    public static IReadOnlyList<DashboardCardPreference> Normalize(
        IReadOnlyList<DashboardCardPreference>? preferences)
    {
        if (preferences is null)
        {
            return Cards;
        }

        var definitionsByKind = Definitions.ToDictionary(definition => definition.Kind);
        var seen = new HashSet<DashboardCardKind>();
        var normalized = new List<DashboardCardPreference>(Definitions.Length);
        foreach (DashboardCardPreference preference in preferences)
        {
            if (!definitionsByKind.ContainsKey(preference.Kind) || !seen.Add(preference.Kind))
            {
                continue;
            }

            normalized.Add(preference);
        }

        foreach (CardDefinition definition in Definitions)
        {
            if (seen.Add(definition.Kind))
            {
                normalized.Add(new DashboardCardPreference(definition.Kind, false));
            }
        }

        return normalized;
    }

    public static IReadOnlyList<DashboardCardOption> CreateOptions(
        IReadOnlyList<DashboardCardPreference>? preferences)
    {
        IReadOnlyDictionary<DashboardCardKind, CardDefinition> definitions =
            Definitions.ToDictionary(definition => definition.Kind);
        return Normalize(preferences)
            .Select(preference =>
            {
                CardDefinition definition = definitions[preference.Kind];
                return new DashboardCardOption(
                    preference.Kind,
                    definition.Label,
                    definition.Description,
                    preference.IsPinned);
            })
            .ToArray();
    }
}
