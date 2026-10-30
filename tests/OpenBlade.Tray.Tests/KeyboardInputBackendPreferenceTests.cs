namespace OpenBlade.Tray.Tests;

public sealed class KeyboardInputBackendPreferenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.KeyboardBackend.Tests", Guid.NewGuid().ToString("N"));
    private string PreferencePath => Path.Combine(directory, "preferences.json");

    [Fact]
    public async Task MissingSelectionsPreserveExistingBackends()
    {
        var store = new UiPreferenceStore(PreferencePath);
        AssertDefaults(await store.LoadAsync());
        UiInputPreferences missing = await store.LoadInputPreferencesAsync();
        Assert.Equal(KeyboardInputBackend.RazerDriver, missing.SnapTapBackend);
        Assert.Equal(KeyboardInputBackend.WindowsHook, missing.FunctionRowBackend);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PreferencePath, "{}");
        AssertDefaults(await store.LoadAsync());
        missing = await store.LoadInputPreferencesAsync();
        Assert.Equal(KeyboardInputBackend.RazerDriver, missing.SnapTapBackend);
        Assert.Equal(KeyboardInputBackend.WindowsHook, missing.FunctionRowBackend);
    }

    [Theory]
    [InlineData(KeyboardInputBackend.WindowsHook, KeyboardInputBackend.RazerDriver)]
    [InlineData(KeyboardInputBackend.RazerDriver, KeyboardInputBackend.WindowsHook)]
    [InlineData(KeyboardInputBackend.WindowsHook, KeyboardInputBackend.WindowsHook)]
    [InlineData(KeyboardInputBackend.RazerDriver, KeyboardInputBackend.RazerDriver)]
    public async Task SelectionsRoundTripIndependently(KeyboardInputBackend snapTap, KeyboardInputBackend functionRow)
    {
        var store = new UiPreferenceStore(PreferencePath);
        await store.SaveAsync(UiPreferences.Default with { SnapTapBackend = snapTap, FunctionRowBackend = functionRow });
        UiPreferences saved = await store.LoadAsync();
        UiInputPreferences input = await store.LoadInputPreferencesAsync();
        Assert.Equal(snapTap, saved.EffectiveSnapTapBackend);
        Assert.Equal(functionRow, saved.EffectiveFunctionRowBackend);
        Assert.Equal(snapTap, input.SnapTapBackend);
        Assert.Equal(functionRow, input.FunctionRowBackend);
    }

    [Theory]
    [InlineData("\"FutureBackend\"")]
    [InlineData("99")]
    [InlineData("null")]
    public async Task FutureSelectionsPauseWithoutSubstitutingOrChangingFile(string value)
    {
        Directory.CreateDirectory(directory);
        string json = "{\"snapTapEnabled\":true,\"snapTapBackend\":" + value + ",\"functionRowBackend\":" + value + "}";
        await File.WriteAllTextAsync(PreferencePath, json);
        var store = new UiPreferenceStore(PreferencePath);
        UiPreferences saved = await store.LoadAsync();
        UiInputPreferences input = await store.LoadInputPreferencesAsync();
        Assert.True(saved.SnapTapEnabled);
        Assert.Equal(KeyboardInputBackend.Unknown, saved.EffectiveSnapTapBackend);
        Assert.Equal(KeyboardInputBackend.Unknown, saved.EffectiveFunctionRowBackend);
        Assert.Equal(KeyboardInputBackend.Unknown, input.SnapTapBackend);
        Assert.Equal(KeyboardInputBackend.Unknown, input.FunctionRowBackend);
        Assert.Equal(json, await File.ReadAllTextAsync(PreferencePath));
    }

    [Fact]
    public void InvalidInMemorySelectionsNormalizeToUnknown()
    {
        var preferences = UiPreferences.Default with { SnapTapBackend = (KeyboardInputBackend)99, FunctionRowBackend = (KeyboardInputBackend)(-1) };
        Assert.Equal(KeyboardInputBackend.Unknown, preferences.EffectiveSnapTapBackend);
        Assert.Equal(KeyboardInputBackend.Unknown, preferences.EffectiveFunctionRowBackend);
    }

    private static void AssertDefaults(UiPreferences value)
    {
        Assert.Equal(KeyboardInputBackend.RazerDriver, value.EffectiveSnapTapBackend);
        Assert.Equal(KeyboardInputBackend.WindowsHook, value.EffectiveFunctionRowBackend);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
