using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PageLayoutPreferenceTests
{
    [Fact]
    public void DefaultsCoverEveryCustomizablePageWithVisibleSections()
    {
        Assert.Equal(
            Enum.GetValues<CustomizablePageKind>(),
            PageLayoutPreferenceDefaults.Layouts.Select(layout => layout.Page));
        Assert.All(
            PageLayoutPreferenceDefaults.Layouts,
            layout =>
            {
                Assert.NotEmpty(layout.Sections);
                Assert.All(layout.Sections, section => Assert.True(section.IsVisible));
            });
    }

    [Fact]
    public void NormalizePreservesKnownOrderAndVisibilityThenAppendsNewSections()
    {
        PageLayoutPreference[] source =
        [
            new(
                CustomizablePageKind.Laptop,
                [
                    new(PageSectionKind.LaptopShortcutKeys, false),
                    new(PageSectionKind.LaptopPowerMode, true),
                    new(PageSectionKind.LaptopShortcutKeys, true),
                    new(PageSectionKind.SettingsBehavior, false),
                ]),
            new(
                CustomizablePageKind.Laptop,
                [new(PageSectionKind.LaptopFans, false)]),
        ];

        PageLayoutPreference laptop = PageLayoutPreferenceDefaults
            .Normalize(source)
            .Single(layout => layout.Page == CustomizablePageKind.Laptop);

        Assert.Equal(
            [
                PageSectionKind.LaptopShortcutKeys,
                PageSectionKind.LaptopPowerMode,
                PageSectionKind.LaptopFans,
                PageSectionKind.LaptopBattery,
                PageSectionKind.LaptopKeyboardBehavior,
                PageSectionKind.LaptopStartupAnimation,
            ],
            laptop.Sections.Select(section => section.Kind));
        Assert.False(laptop.Sections[0].IsVisible);
        Assert.True(laptop.Sections[1].IsVisible);
        Assert.All(laptop.Sections.Skip(2), section => Assert.True(section.IsVisible));
    }

    [Fact]
    public void NullLayoutsMigrateToCurrentDefaults()
    {
        var preferences = new UiPreferences(
            ThemePreference.FollowWindows,
            PageLayouts: null);

        Assert.Equal(
            PageLayoutPreferenceDefaults.Layouts,
            preferences.EffectivePageLayouts);
    }

    [Fact]
    public async Task StoreRoundTripsIndependentPageLayouts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"OpenBlade.PageLayouts.{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "preferences.json");
        try
        {
            var store = new UiPreferenceStore(path);
            PageLayoutPreference[] layouts =
            [
                new(
                    CustomizablePageKind.Settings,
                    [
                        new(PageSectionKind.SettingsShutdown, false),
                        new(PageSectionKind.SettingsAppearance, true),
                        new(PageSectionKind.SettingsBehavior, true),
                    ]),
                new(
                    CustomizablePageKind.About,
                    [
                        new(PageSectionKind.AboutSupport, true),
                        new(PageSectionKind.AboutOverview, false),
                    ]),
            ];

            await store.SaveAsync(new UiPreferences(
                ThemePreference.Dark,
                PageLayouts: layouts));
            UiPreferences reloaded = await store.LoadAsync();

            PageLayoutPreference settings = reloaded.EffectivePageLayouts
                .Single(layout => layout.Page == CustomizablePageKind.Settings);
            Assert.Equal(
                [
                    PageSectionKind.SettingsUpdates,
                    PageSectionKind.SettingsShutdown,
                    PageSectionKind.SettingsAppearance,
                    PageSectionKind.SettingsBehavior,
                ],
                settings.Sections.Select(section => section.Kind));
            Assert.False(settings.Sections[1].IsVisible);

            PageLayoutPreference about = reloaded.EffectivePageLayouts
                .Single(layout => layout.Page == CustomizablePageKind.About);
            Assert.Equal(PageSectionKind.AboutSupport, about.Sections[0].Kind);
            Assert.Equal(PageSectionKind.AboutOverview, about.Sections[1].Kind);
            Assert.False(about.Sections[1].IsVisible);
            Assert.All(about.Sections.Skip(2), section => Assert.True(section.IsVisible));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EveryNonDashboardPageExposesSharedCustomizationContract()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "OpenBlade.Tray", "MainWindow.xaml"));
        string window = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "OpenBlade.Tray", "MainWindow.xaml.cs"));

        foreach (string page in Enum.GetNames<CustomizablePageKind>()
                     .Where(page => page != nameof(CustomizablePageKind.Profiles)))
        {
            Assert.Contains($"Tag=\"{page}\"", xaml, StringComparison.Ordinal);
            Assert.Contains(
                $"AutomationProperties.Name=\"Customize {page} page\"",
                xaml,
                StringComparison.Ordinal);
        }
        Assert.DoesNotContain(
            "AutomationProperties.Name=\"Customize Profiles page\"",
            xaml,
            StringComparison.Ordinal);

        Assert.Contains("x:Key=\"PageSectionOptionTemplate\"", xaml, StringComparison.Ordinal);
        foreach (string elementName in new[]
                 {
                     "ProfilesSectionHost",
                     "ProfilesComparisonSection",
                     "DisplayCardsHost",
                     "LaptopCardsHost",
                     "ShortcutKeysSection",
                     "LightingSectionHost",
                     "LightingKeyboardSection",
                     "LightingLogoSection",
                     "LightingPowerSavingSection",
                     "SettingsSectionHost",
                     "SettingsBehaviorSection",
                     "SettingsAppearanceSection",
                     "SettingsUpdatesSection",
                     "SettingsShutdownSection",
                     "AboutSectionHost",
                     "AboutOverviewSection",
                     "AboutVersionsSection",
                     "AboutDeviceInformationSection",
                     "AboutSupportSection",
                     "AboutTroubleshootingSection",
                 })
        {
            Assert.Contains($"x:Name=\"{elementName}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("CanDragItems = true", window, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems = true", window, StringComparison.Ordinal);
        Assert.Contains("preferences.EffectivePageLayouts", window, StringComparison.Ordinal);
        Assert.Contains("PageLayouts: pageSections", window, StringComparison.Ordinal);
        Assert.Contains(".Where(section => section.IsVisible)", window, StringComparison.Ordinal);
        Assert.Contains("host.Children.Remove(child);", window, StringComparison.Ordinal);
        Assert.Contains("host.Children.Add(section);", window, StringComparison.Ordinal);
        Assert.Contains("RefreshPageSections(page);", window, StringComparison.Ordinal);
        Assert.Contains("host.Children.Move(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("PageLayoutRestartRequested", window, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAboutUpdatesPreferenceMigratesToSettings()
    {
        PageLayoutPreference[] source =
        [
            new(
                CustomizablePageKind.Settings,
                [new(PageSectionKind.SettingsBehavior, true)]),
            new(
                CustomizablePageKind.About,
                [
                    new(PageSectionKind.AboutUpdates, false),
                    new(PageSectionKind.AboutOverview, true),
                ]),
        ];

        IReadOnlyList<PageLayoutPreference> normalized =
            PageLayoutPreferenceDefaults.Normalize(source);
        PageSectionPreference updates = normalized
            .Single(layout => layout.Page == CustomizablePageKind.Settings)
            .Sections
            .Single(section => section.Kind == PageSectionKind.SettingsUpdates);
        PageLayoutPreference about = normalized
            .Single(layout => layout.Page == CustomizablePageKind.About);

        Assert.False(updates.IsVisible);
        Assert.Equal(
            [
                PageSectionKind.SettingsBehavior,
                PageSectionKind.SettingsAppearance,
                PageSectionKind.SettingsUpdates,
                PageSectionKind.SettingsShutdown,
            ],
            normalized
                .Single(layout => layout.Page == CustomizablePageKind.Settings)
                .Sections
                .Select(section => section.Kind));
        Assert.DoesNotContain(
            about.Sections,
            section => section.Kind == PageSectionKind.AboutUpdates);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OpenBlade.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the OpenBlade repository root.");
    }
}
