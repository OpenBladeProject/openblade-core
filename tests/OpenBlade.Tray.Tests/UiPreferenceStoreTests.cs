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
            ]));

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
                TrayHoverLabelItemKind.GpuTemperature,
                TrayHoverLabelItemKind.ActiveProfile,
                TrayHoverLabelItemKind.BatteryPower,
                TrayHoverLabelItemKind.CpuTemperature,
                TrayHoverLabelItemKind.Fan1Speed,
                TrayHoverLabelItemKind.Fan2Speed,
            ],
            reloaded.EffectiveTrayHoverLabelItems.Select(item => item.Kind));
        Assert.Equal(
            [true, true, false, false, false, false],
            reloaded.EffectiveTrayHoverLabelItems.Select(item => item.IsEnabled));
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
                item => item.Kind == TrayHoverLabelItemKind.ActiveProfile).IsEnabled);
        Assert.Equal(
            powerEnabled,
            preferences.EffectiveTrayHoverLabelItems.Single(
                item => item.Kind == TrayHoverLabelItemKind.BatteryPower).IsEnabled);
        Assert.All(
            preferences.EffectiveTrayHoverLabelItems.Where(item =>
                item.Kind is not TrayHoverLabelItemKind.ActiveProfile
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
