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

    private static CardDefinition[] Definitions =>
    [
        new(
            DashboardCardKind.DeviceStatus,
            TrayText.Get("Device status and live readings"),
            TrayText.Get("Connection, active settings, and the live readings you selected."),
            true),
        new(
            DashboardCardKind.PowerMode,
            TrayText.Get("Power mode"),
            TrayText.Get("Choose the active power mode and custom CPU or GPU levels."),
            false),
        new(
            DashboardCardKind.Fans,
            TrayText.Get("Fans"),
            TrayText.Get("Choose automatic, fixed, maximum, or manual fan control."),
            false),
        new(
            DashboardCardKind.Battery,
            TrayText.Get("Battery"),
            TrayText.Get("Manage battery protection, charge limit, and temporary full charge."),
            false),
        new(
            DashboardCardKind.KeyboardBehavior,
            TrayText.Get("Keyboard behavior"),
            TrayText.Get("Set function-key behavior and gaming mode."),
            false),
        new(
            DashboardCardKind.StartupAnimation,
            TrayText.Get("Startup animation"),
            TrayText.Get("Choose whether the keyboard animation plays during startup."),
            false),
        new(
            DashboardCardKind.Lighting,
            TrayText.Get("Lighting"),
            TrayText.Get("Control keyboard effects, brightness, and laptop logo lighting."),
            false),
        new(
            DashboardCardKind.Display,
            TrayText.Get("Display"),
            TrayText.Get("Control refresh rate, HDR, and the color profile."),
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
