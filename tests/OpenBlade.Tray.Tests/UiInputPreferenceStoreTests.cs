using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class UiInputPreferenceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "OpenBlade.Tray.Tests", Guid.NewGuid().ToString("N"));

    private string PreferencePath => Path.Combine(directory, "preferences.json");

    [Fact]
    public async Task LoadInputPreferencesAsync_IgnoresUnknownUiValuesWithoutChangingFile()
    {
        Directory.CreateDirectory(directory);
        string json = """
            {
              "primaryNavigationItems": ["Laptop", "ThxAudio", "FuturePage"],
              "theme": "FutureTheme",
              "thxAudioIntegrationEnabled": true,
              "specialKeyBindings": [{"slot":"M1","action":{"kind":"Disabled"}}],
              "snapTapEnabled": true
            }
            """;
        await File.WriteAllTextAsync(PreferencePath, json);
        byte[] before = await File.ReadAllBytesAsync(PreferencePath);

        UiInputPreferences input = await new UiPreferenceStore(PreferencePath)
            .LoadInputPreferencesAsync();

        Assert.True(input.SnapTapEnabled);
        Assert.Equal(SpecialKeyActionKind.Disabled, Assert.Single(input.SpecialKeyBindings!).Action.Kind);
        Assert.Equal(before, await File.ReadAllBytesAsync(PreferencePath));
    }

    [Fact]
    public async Task LoadInputPreferencesAsync_MissingFileAndMissingFieldsDefaultOff()
    {
        var store = new UiPreferenceStore(PreferencePath);
        Assert.False((await store.LoadInputPreferencesAsync()).SnapTapEnabled);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PreferencePath, "{}");
        UiInputPreferences input = await store.LoadInputPreferencesAsync();
        Assert.False(input.SnapTapEnabled);
        Assert.Null(input.SpecialKeyBindings);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"snapTapEnabled\":\"yes\"}")]
    [InlineData("{\"snapTapEnabled\":null}")]
    [InlineData("{\"specialKeyBindings\":[{\"slot\":\"M1\",\"action\":{\"kind\":\"FutureAction\"}}]}")]
    public async Task LoadInputPreferencesAsync_InvalidInputFailsWithoutChangingFile(string json)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PreferencePath, json);
        await Assert.ThrowsAnyAsync<JsonException>(
            () => new UiPreferenceStore(PreferencePath).LoadInputPreferencesAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(PreferencePath));
    }

    [Fact]
    public async Task LoadInputPreferencesAsync_OversizedAndCanceledReadsFail()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(PreferencePath, new byte[UiPreferenceStore.MaximumFileSizeBytes + 1]);
        var store = new UiPreferenceStore(PreferencePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadInputPreferencesAsync());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.LoadInputPreferencesAsync(canceled.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
