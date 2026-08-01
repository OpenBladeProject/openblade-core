using System.ComponentModel;

namespace OpenBlade.Tray;

public enum CustomizablePageKind
{
    Profiles,
    Display,
    Laptop,
    Lighting,
    Settings,
    About,
}

public enum PageSectionKind
{
    ProfilesComparison,
    DisplayControls,
    LaptopPowerMode,
    LaptopFans,
    LaptopBattery,
    LaptopKeyboardBehavior,
    LaptopStartupAnimation,
    LaptopShortcutKeys,
    LightingKeyboard,
    LightingLogo,
    LightingPowerSaving,
    SettingsBehavior,
    SettingsAppearance,
    SettingsUpdates,
    SettingsShutdown,
    AboutOverview,
    AboutVersions,
    // Retained so existing string-enum preferences can migrate to
    // SettingsUpdates without invalidating the complete preference file.
    AboutUpdates,
    AboutDeviceInformation,
    AboutSupport,
    AboutTroubleshooting,
}

public sealed record PageSectionPreference(
    PageSectionKind Kind,
    bool IsVisible);

public sealed record PageLayoutPreference(
    CustomizablePageKind Page,
    IReadOnlyList<PageSectionPreference> Sections);

public sealed class PageSectionOption : INotifyPropertyChanged
{
    private bool isVisible;

    public PageSectionOption(
        PageSectionKind kind,
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

    public PageSectionKind Kind { get; }

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

public static class PageLayoutPreferenceDefaults
{
    private sealed record SectionDefinition(
        CustomizablePageKind Page,
        PageSectionKind Kind,
        string Label,
        string Description);

    private static readonly SectionDefinition[] Definitions =
    [
        new(
            CustomizablePageKind.Profiles,
            PageSectionKind.ProfilesComparison,
            "Saved profile comparison",
            "Compare the settings stored in AC, USB-C, and Battery profiles."),
        new(
            CustomizablePageKind.Display,
            PageSectionKind.DisplayControls,
            "Built-in display",
            "Control refresh rate, HDR, and color profile."),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopPowerMode,
            "Power mode",
            "Choose the laptop's performance and power behavior."),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopFans,
            "Fans",
            "Choose automatic, fixed, maximum, or manual fan control."),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopBattery,
            "Battery",
            "Manage battery protection and temporary full charge."),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopKeyboardBehavior,
            "Keyboard behavior",
            "Set function-key behavior and gaming mode."),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopStartupAnimation,
            "Startup animation",
            "Choose whether the keyboard animation plays during startup."),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopShortcutKeys,
            "Shortcut keys",
            "Choose the actions assigned to M1 through M5."),
        new(
            CustomizablePageKind.Lighting,
            PageSectionKind.LightingKeyboard,
            "Keyboard",
            "Control keyboard effects, brightness, colors, and direction."),
        new(
            CustomizablePageKind.Lighting,
            PageSectionKind.LightingLogo,
            "Laptop logo",
            "Choose the lighting mode for the logo on the lid."),
        new(
            CustomizablePageKind.Lighting,
            PageSectionKind.LightingPowerSaving,
            "Switch off lighting",
            "Turn keyboard lighting off with the display or after inactivity."),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsBehavior,
            "Behavior",
            "Configure live readings, sign-in, and lighting ownership."),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsAppearance,
            "Appearance",
            "Configure theme, accent color, navigation, and tray text."),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsUpdates,
            "Updates",
            "Choose when OpenBlade checks GitHub for new releases."),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsShutdown,
            "Shutdown",
            "Stop OpenBlade until Windows restarts."),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutOverview,
            "OpenBlade",
            "Show the app identity and project relationship."),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutVersions,
            "Versions",
            "Show the installed app and service versions."),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutDeviceInformation,
            "Device information",
            "Show detected laptop and firmware details."),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutSupport,
            "Support",
            "Show diagnostics, documentation, and project links."),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutTroubleshooting,
            "Troubleshooting",
            "Show service, laptop, power, and software status."),
    ];

    public static IReadOnlyList<PageLayoutPreference> Layouts { get; } =
        Enum.GetValues<CustomizablePageKind>()
            .Select(page => new PageLayoutPreference(
                page,
                Definitions
                    .Where(definition => definition.Page == page)
                    .Select(definition => new PageSectionPreference(
                        definition.Kind,
                        true))
                    .ToArray()))
            .ToArray();

    public static IReadOnlyList<PageLayoutPreference> Normalize(
        IReadOnlyList<PageLayoutPreference>? layouts)
    {
        if (layouts is null)
        {
            return Layouts;
        }

        var sourceByPage = new Dictionary<CustomizablePageKind, PageLayoutPreference>();
        foreach (PageLayoutPreference layout in layouts)
        {
            if (Enum.IsDefined(layout.Page))
            {
                sourceByPage.TryAdd(layout.Page, layout);
            }
        }

        bool hasCurrentUpdatesPreference =
            sourceByPage.TryGetValue(
                CustomizablePageKind.Settings,
                out PageLayoutPreference? settingsLayout)
            && settingsLayout.Sections.Any(section =>
                section.Kind == PageSectionKind.SettingsUpdates);
        bool? legacyUpdatesVisibility = sourceByPage.TryGetValue(
                CustomizablePageKind.About,
                out PageLayoutPreference? aboutLayout)
            ? aboutLayout.Sections
                .FirstOrDefault(section =>
                    section.Kind == PageSectionKind.AboutUpdates)
                ?.IsVisible
            : null;

        return Enum.GetValues<CustomizablePageKind>()
            .Select(page =>
            {
                IReadOnlyList<PageSectionPreference> sections = NormalizePage(
                    page,
                    sourceByPage.TryGetValue(page, out PageLayoutPreference? layout)
                        ? layout.Sections
                        : null);
                if (page == CustomizablePageKind.Settings
                    && !hasCurrentUpdatesPreference
                    && legacyUpdatesVisibility is { } isVisible)
                {
                    sections = sections
                        .Select(section =>
                            section.Kind == PageSectionKind.SettingsUpdates
                                ? section with { IsVisible = isVisible }
                                : section)
                        .ToArray();
                }

                return new PageLayoutPreference(page, sections);
            })
            .ToArray();
    }

    public static IReadOnlyList<PageSectionPreference> NormalizePage(
        CustomizablePageKind page,
        IReadOnlyList<PageSectionPreference>? sections)
    {
        SectionDefinition[] definitions = Definitions
            .Where(definition => definition.Page == page)
            .ToArray();
        if (sections is null)
        {
            return definitions
                .Select(definition => new PageSectionPreference(
                    definition.Kind,
                    true))
                .ToArray();
        }

        var definitionsByKind = definitions.ToDictionary(definition => definition.Kind);
        var seen = new HashSet<PageSectionKind>();
        var normalized = new List<PageSectionPreference>(definitions.Length);
        foreach (PageSectionPreference section in sections)
        {
            if (!definitionsByKind.ContainsKey(section.Kind) || !seen.Add(section.Kind))
            {
                continue;
            }

            normalized.Add(section);
        }

        foreach (SectionDefinition definition in definitions)
        {
            if (seen.Add(definition.Kind))
            {
                normalized.Add(new PageSectionPreference(definition.Kind, true));
            }
        }

        return normalized;
    }

    public static IReadOnlyList<PageSectionOption> CreateOptions(
        CustomizablePageKind page,
        IReadOnlyList<PageSectionPreference>? sections)
    {
        IReadOnlyDictionary<PageSectionKind, SectionDefinition> definitions =
            Definitions
                .Where(definition => definition.Page == page)
                .ToDictionary(definition => definition.Kind);
        return NormalizePage(page, sections)
            .Select(section =>
            {
                SectionDefinition definition = definitions[section.Kind];
                return new PageSectionOption(
                    section.Kind,
                    definition.Label,
                    definition.Description,
                    section.IsVisible);
            })
            .ToArray();
    }
}
