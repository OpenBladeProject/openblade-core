using System.Text.Json;

namespace OpenSynapse.Tray.Tests;

public sealed class UiPreferenceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenSynapse.Tray.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsFollowWindows()
    {
        UiPreferenceStore store = new(Path.Combine(directory, "preferences.json"));

        UiPreferences preferences = await store.LoadAsync();

        Assert.Equal(ThemePreference.FollowWindows, preferences.Theme);
    }

    [Fact]
    public async Task SaveAsync_PersistsThemeAsJson()
    {
        string path = Path.Combine(directory, "preferences.json");
        UiPreferenceStore store = new(path);

        await store.SaveAsync(new UiPreferences(ThemePreference.Dark));

        UiPreferences reloaded = await store.LoadAsync();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.Equal("Dark", document.RootElement.GetProperty("theme").GetString());
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
