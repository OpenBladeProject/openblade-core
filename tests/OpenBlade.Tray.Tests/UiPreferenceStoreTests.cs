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
        Assert.False(preferences.KeepManualChangesUntilCleared);
        Assert.True(preferences.LiveReadingsEnabled);
        Assert.Equal(
            TrayHoverLabelMode.ProfileAndPowerFlow,
            preferences.TrayHoverLabelMode);
    }

    [Fact]
    public async Task SaveAsync_PersistsThemeAsJson()
    {
        string path = Path.Combine(directory, "preferences.json");
        UiPreferenceStore store = new(path);

        await store.SaveAsync(new UiPreferences(
            ThemePreference.Dark,
            KeepManualChangesUntilCleared: true,
            LiveReadingsEnabled: false,
            TrayHoverLabelMode: TrayHoverLabelMode.BatteryPowerFlow));

        UiPreferences reloaded = await store.LoadAsync();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.True(reloaded.KeepManualChangesUntilCleared);
        Assert.False(reloaded.LiveReadingsEnabled);
        Assert.Equal(TrayHoverLabelMode.BatteryPowerFlow, reloaded.TrayHoverLabelMode);
        Assert.Equal("Dark", document.RootElement.GetProperty("theme").GetString());
        Assert.True(document.RootElement.GetProperty("keepManualChangesUntilCleared").GetBoolean());
        Assert.False(document.RootElement.GetProperty("liveReadingsEnabled").GetBoolean());
        Assert.Equal(
            "BatteryPowerFlow",
            document.RootElement.GetProperty("trayHoverLabelMode").GetString());
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
    public async Task LoadAsync_PreviousSchemaDefaultsLiveReadingsOn()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","keepManualChangesUntilCleared":true}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(ThemePreference.Dark, preferences.Theme);
        Assert.True(preferences.KeepManualChangesUntilCleared);
        Assert.True(preferences.LiveReadingsEnabled);
        Assert.Equal(
            TrayHoverLabelMode.ProfileAndPowerFlow,
            preferences.TrayHoverLabelMode);
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
