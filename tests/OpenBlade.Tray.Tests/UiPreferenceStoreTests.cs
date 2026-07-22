using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class UiPreferenceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Tray.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsFollowWindows()
    {
        UiPreferenceStore store = new(Path.Combine(directory, "preferences.json"));

        UiPreferences preferences = await store.LoadAsync();

        Assert.Equal(ThemePreference.FollowWindows, preferences.Theme);
        Assert.True(preferences.LiveReadingsEnabled);
        Assert.Equal(
            LiveReadingsCadence.EveryTwoSeconds,
            preferences.EffectiveLiveReadingsCadence);
        Assert.Equal(
            TrayHoverLabelMode.ProfileAndPowerFlow,
            preferences.TrayHoverLabelMode);
        Assert.False(preferences.StartMinimized);
        Assert.Equal(
            TelemetryPreferenceDefaults.DashboardItems,
            preferences.EffectiveDashboardTelemetryItems);
        Assert.Equal(
            TelemetryPreferenceDefaults.TrayHoverItems,
            preferences.EffectiveTrayHoverLabelItems);
        Assert.Equal(
            DashboardCardPreferenceDefaults.Cards,
            preferences.EffectiveDashboardCards);
        Assert.Equal(
            PrimaryNavigationPreferenceDefaults.Items,
            preferences.EffectivePrimaryNavigationItems);
        Assert.Equal(AppAccentColorMode.RazerGreen, preferences.EffectiveAccentColorMode);
        Assert.Equal(
            AppAccentColorPreferenceDefaults.RazerGreenHex,
            preferences.EffectiveCustomAccentColor);
    }

    [Fact]
    public async Task SaveAsync_PersistsThemeAsJson()
    {
        string path = Path.Combine(directory, "preferences.json");
        UiPreferenceStore store = new(path);

        await store.SaveAsync(new UiPreferences(
            ThemePreference.Dark,
            LiveReadingsEnabled: false,
            TrayHoverLabelMode: TrayHoverLabelMode.BatteryPowerFlow,
            LiveReadingsRefreshCadence: LiveReadingsCadence.EveryTenSeconds,
            StartMinimized: true,
            DashboardTelemetryItems:
            [
                LiveReadingItemKind.GpuTemperature,
                LiveReadingItemKind.BatteryPower,
            ],
            TrayHoverLabelItems:
            [
                new(TrayHoverLabelItemKind.GpuTemperature, true),
                new(TrayHoverLabelItemKind.ActiveProfile, true),
                new(TrayHoverLabelItemKind.BatteryPower, false),
            ],
            DashboardCards:
            [
                new(DashboardCardKind.Battery, true),
                new(DashboardCardKind.DeviceStatus, false),
            ],
            PrimaryNavigationItems:
            [
                PrimaryNavigationItemKind.Settings,
                PrimaryNavigationItemKind.Display,
                PrimaryNavigationItemKind.Laptop,
            ],
            AccentColorMode: AppAccentColorMode.Custom,
            CustomAccentColor: "#123abc"));

        UiPreferences reloaded = await store.LoadAsync();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.False(reloaded.LiveReadingsEnabled);
        Assert.Equal(LiveReadingsCadence.Off, reloaded.EffectiveLiveReadingsCadence);
        Assert.Equal(TrayHoverLabelMode.BatteryPowerFlow, reloaded.TrayHoverLabelMode);
        Assert.True(reloaded.StartMinimized);
        Assert.Equal(
            [LiveReadingItemKind.BatteryPower, LiveReadingItemKind.GpuTemperature],
            reloaded.EffectiveDashboardTelemetryItems);
        Assert.Equal(
            [
                TrayHoverLabelItemKind.AppName,
                TrayHoverLabelItemKind.GpuTemperature,
                TrayHoverLabelItemKind.PowerSource,
                TrayHoverLabelItemKind.PowerMode,
                TrayHoverLabelItemKind.BatteryPower,
                TrayHoverLabelItemKind.CpuTemperature,
                TrayHoverLabelItemKind.Fan1Speed,
                TrayHoverLabelItemKind.Fan2Speed,
            ],
            reloaded.EffectiveTrayHoverLabelItems.Select(item => item.Kind));
        Assert.Equal(
            [true, true, true, true, false, false, false, false],
            reloaded.EffectiveTrayHoverLabelItems.Select(item => item.IsEnabled));
        Assert.Equal(
            [DashboardCardKind.Battery, DashboardCardKind.DeviceStatus],
            reloaded.EffectiveDashboardCards.Take(2).Select(item => item.Kind));
        Assert.Equal(
            [true, false],
            reloaded.EffectiveDashboardCards.Take(2).Select(item => item.IsPinned));
        Assert.Equal(
            [
                PrimaryNavigationItemKind.Settings,
                PrimaryNavigationItemKind.Display,
                PrimaryNavigationItemKind.Laptop,
                PrimaryNavigationItemKind.Profiles,
                PrimaryNavigationItemKind.Lighting,
            ],
            reloaded.EffectivePrimaryNavigationItems);
        Assert.Equal(AppAccentColorMode.Custom, reloaded.EffectiveAccentColorMode);
        Assert.Equal("#123ABC", reloaded.EffectiveCustomAccentColor);
        Assert.Equal("Dark", document.RootElement.GetProperty("theme").GetString());
        Assert.False(document.RootElement.TryGetProperty(
            "keepManualChangesUntilCleared",
            out _));
        Assert.False(document.RootElement.GetProperty("liveReadingsEnabled").GetBoolean());
        Assert.Equal(
            "BatteryPowerFlow",
            document.RootElement.GetProperty("trayHoverLabelMode").GetString());
        Assert.Equal(
            "EveryTenSeconds",
            document.RootElement.GetProperty("liveReadingsRefreshCadence").GetString());
        Assert.True(document.RootElement.GetProperty("startMinimized").GetBoolean());
        Assert.Equal(
            "GpuTemperature",
            document.RootElement.GetProperty("dashboardTelemetryItems")[0].GetString());
        Assert.Equal(
            "GpuTemperature",
            document.RootElement.GetProperty("trayHoverLabelItems")[0]
                .GetProperty("kind").GetString());
        Assert.Equal(
            "Battery",
            document.RootElement.GetProperty("dashboardCards")[0]
                .GetProperty("kind").GetString());
        Assert.Equal(
            "Settings",
            document.RootElement.GetProperty("primaryNavigationItems")[0].GetString());
        Assert.Equal(
            "Custom",
            document.RootElement.GetProperty("accentColorMode").GetString());
        Assert.Equal(
            "#123abc",
            document.RootElement.GetProperty("customAccentColor").GetString());
    }

    [Fact]
    public async Task LoadAsync_InvalidCustomAccentFallsBackToRazerGreen()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","accentColorMode":"Custom","customAccentColor":"not-a-color"}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(AppAccentColorMode.Custom, preferences.EffectiveAccentColorMode);
        Assert.Equal(
            AppAccentColorPreferenceDefaults.RazerGreenHex,
            preferences.EffectiveCustomAccentColor);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ReturnsFollowWindows()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{ definitely not json }");
        UiPreferenceStore store = new(path);

        UiPreferences preferences = await store.LoadAsync();

        Assert.Equal(ThemePreference.FollowWindows, preferences.Theme);
    }

    [Fact]
    public async Task LoadAsync_ObsoleteManualPreferenceIsIgnored()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","keepManualChangesUntilCleared":true}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(ThemePreference.Dark, preferences.Theme);
        Assert.True(preferences.LiveReadingsEnabled);
        Assert.Equal(
            LiveReadingsCadence.EveryTwoSeconds,
            preferences.EffectiveLiveReadingsCadence);
        Assert.Equal(
            TrayHoverLabelMode.ProfileAndPowerFlow,
            preferences.TrayHoverLabelMode);
    }

    [Fact]
    public async Task LoadAsync_PreviousDisabledPreferenceRemainsOff()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","liveReadingsEnabled":false}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.False(preferences.LiveReadingsEnabled);
        Assert.Equal(LiveReadingsCadence.Off, preferences.EffectiveLiveReadingsCadence);
    }

    [Theory]
    [InlineData(TrayHoverLabelMode.OpenBlade, false, false)]
    [InlineData(TrayHoverLabelMode.ActiveProfile, true, false)]
    [InlineData(TrayHoverLabelMode.BatteryPowerFlow, false, true)]
    [InlineData(TrayHoverLabelMode.ProfileAndPowerFlow, true, true)]
    public async Task LoadAsync_LegacyHoverModeMigratesToOrderedItems(
        TrayHoverLabelMode mode,
        bool profileEnabled,
        bool powerEnabled)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            $$"""{"theme":"Dark","trayHoverLabelMode":"{{mode}}"}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(
            profileEnabled,
            preferences.EffectiveTrayHoverLabelItems.Single(
                item => item.Kind == TrayHoverLabelItemKind.PowerSource).IsEnabled);
        Assert.Equal(
            profileEnabled,
            preferences.EffectiveTrayHoverLabelItems.Single(
                item => item.Kind == TrayHoverLabelItemKind.PowerMode).IsEnabled);
        Assert.Equal(
            powerEnabled,
            preferences.EffectiveTrayHoverLabelItems.Single(
                item => item.Kind == TrayHoverLabelItemKind.BatteryPower).IsEnabled);
        Assert.True(preferences.EffectiveTrayHoverLabelItems.Single(
            item => item.Kind == TrayHoverLabelItemKind.AppName).IsEnabled);
        Assert.All(
            preferences.EffectiveTrayHoverLabelItems.Where(item =>
                item.Kind is not TrayHoverLabelItemKind.AppName
                    and not TrayHoverLabelItemKind.PowerSource
                    and not TrayHoverLabelItemKind.PowerMode
                    and not TrayHoverLabelItemKind.BatteryPower),
            item => Assert.False(item.IsEnabled));
    }

    [Fact]
    public async Task SaveAsync_ReplacesExistingFileWithoutLeavingTemporaryFiles()
    {
        string path = Path.Combine(directory, "preferences.json");
        UiPreferenceStore store = new(path);
        await store.SaveAsync(new UiPreferences(ThemePreference.Light));

        await store.SaveAsync(new UiPreferences(ThemePreference.Dark));

        Assert.Equal(ThemePreference.Dark, (await store.LoadAsync()).Theme);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
