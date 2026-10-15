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

    private static SectionDefinition[] Definitions =>
    [
        new(
            CustomizablePageKind.Profiles,
            PageSectionKind.ProfilesComparison,
            TrayText.Get("Saved profile comparison"),
            TrayText.Get("Compare the settings stored in AC, low-power, and battery profiles.")),
        new(
            CustomizablePageKind.Display,
            PageSectionKind.DisplayControls,
            TrayText.Get("Built-in display"),
            TrayText.Get("Control refresh rate, HDR, and color profile.")),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopPowerMode,
            TrayText.Get("Power mode"),
            TrayText.Get("Choose the laptop's performance and power behavior.")),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopFans,
            TrayText.Get("Fans"),
            TrayText.Get("Choose automatic, fixed, maximum, or manual fan control.")),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopBattery,
            TrayText.Get("Battery"),
            TrayText.Get("Manage battery protection and temporary full charge.")),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopKeyboardBehavior,
            TrayText.Get("Keyboard behavior"),
            TrayText.Get("Set function-key behavior and gaming mode.")),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopStartupAnimation,
            TrayText.Get("Startup animation"),
            TrayText.Get("Choose whether the keyboard animation plays during startup.")),
        new(
            CustomizablePageKind.Laptop,
            PageSectionKind.LaptopShortcutKeys,
            TrayText.Get("Shortcut keys"),
            TrayText.Get("Choose the actions assigned to M1 through M5.")),
        new(
            CustomizablePageKind.Lighting,
            PageSectionKind.LightingKeyboard,
            TrayText.Get("Keyboard"),
            TrayText.Get("Control keyboard effects, brightness, colors, and direction.")),
        new(
            CustomizablePageKind.Lighting,
            PageSectionKind.LightingLogo,
            TrayText.Get("Laptop logo"),
            TrayText.Get("Choose the lighting mode for the logo on the lid.")),
        new(
            CustomizablePageKind.Lighting,
            PageSectionKind.LightingPowerSaving,
            TrayText.Get("Switch off lighting"),
            TrayText.Get("Turn keyboard lighting off with the display or after inactivity.")),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsBehavior,
            TrayText.Get("Behavior"),
            TrayText.Get("Configure live readings, sign-in, and lighting ownership.")),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsAppearance,
            TrayText.Get("Appearance"),
            TrayText.Get("Configure language, theme, accent color, navigation, and tray text.")),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsUpdates,
            TrayText.Get("Updates"),
            TrayText.Get("Choose when OpenBlade checks GitHub for new releases.")),
        new(
            CustomizablePageKind.Settings,
            PageSectionKind.SettingsShutdown,
            TrayText.Get("Shutdown"),
            TrayText.Get("Stop OpenBlade until Windows restarts.")),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutOverview,
            TrayText.Get("OpenBlade"),
            TrayText.Get("Show the app identity and project relationship.")),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutVersions,
            TrayText.Get("Versions"),
            TrayText.Get("Show the installed app and service versions.")),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutDeviceInformation,
            TrayText.Get("Device information"),
            TrayText.Get("Show detected laptop and firmware details.")),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutSupport,
            TrayText.Get("Support"),
            TrayText.Get("Show diagnostics, documentation, and project links.")),
        new(
            CustomizablePageKind.About,
            PageSectionKind.AboutTroubleshooting,
            TrayText.Get("Troubleshooting"),
            TrayText.Get("Show service, laptop, power, and software status.")),
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
            && settingsLayout.Sections?.Any(section =>
                section.Kind == PageSectionKind.SettingsUpdates) == true;
        bool? legacyUpdatesVisibility = sourceByPage.TryGetValue(
                CustomizablePageKind.About,
                out PageLayoutPreference? aboutLayout)
            ? aboutLayout.Sections?
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
                    && !hasCurrentUpdatesPreference)
                {
                    var migrated = sections.ToList();
                    int updatesIndex = migrated.FindIndex(section =>
                        section.Kind == PageSectionKind.SettingsUpdates);
                    PageSectionPreference updates = migrated[updatesIndex];
                    migrated.RemoveAt(updatesIndex);
                    int shutdownIndex = migrated.FindIndex(section =>
                        section.Kind == PageSectionKind.SettingsShutdown);
                    migrated.Insert(
                        shutdownIndex >= 0 ? shutdownIndex : migrated.Count,
                        legacyUpdatesVisibility is { } isVisible
                            ? updates with { IsVisible = isVisible }
                            : updates);
                    sections = migrated;
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
