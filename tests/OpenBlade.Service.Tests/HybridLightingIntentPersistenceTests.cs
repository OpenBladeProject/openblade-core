using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class HybridLightingIntentPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.HybridLightingIntentTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(25, false)]
    [InlineData(26, true)]
    public void ProfilesMigrateMissingOptInWithoutChangingSavedEffect(int schema, bool enabled)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings lighting = profiles.PluggedIn.Lighting with { ControlsKeyboardEffect = enabled };
        profiles = profiles with { PluggedIn = profiles.PluggedIn with { Lighting = lighting } };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new ProfileDocument(schema, profiles), ProfileStore.SerializerOptions);
        var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
        ProfileSet loaded = store.ReadDocument(json, out bool migrated);
        Assert.Equal(enabled, loaded.PluggedIn.Lighting.ControlsKeyboardEffect);
        Assert.Equal(lighting, loaded.PluggedIn.Lighting);
        Assert.Equal(schema != ProfileStore.CurrentSchemaVersion, migrated);
    }

    [Theory]
    [InlineData("controlsKeyboardEffect")]
    [InlineData("ControlsKeyboardEffect")]
    [InlineData("CONTROLSKEYBOARDEFFECT")]
    public void OldProfileSchemaCannotSmuggleFullEffectOptIn(string property)
    {
        var document = JsonSerializer.SerializeToNode(new ProfileDocument(25, ProfileDefaults.CreateSafeDefaults()), ProfileStore.SerializerOptions)!;
        document["profiles"]!["pluggedIn"]!["lighting"]![property] = true;
        var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
        Assert.Throws<InvalidDataException>(() => store.ReadDocument(JsonSerializer.SerializeToUtf8Bytes(document)));
    }

    [Fact]
    public void CurrentProfileRejectsMalformedOptInAndFutureSchema()
    {
        var document = JsonSerializer.SerializeToNode(new ProfileDocument(26, ProfileDefaults.CreateSafeDefaults()), ProfileStore.SerializerOptions)!;
        document["profiles"]!["pluggedIn"]!["lighting"]!["controlsKeyboardEffect"] = "true";
        var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
        Assert.Throws<JsonException>(() => store.ReadDocument(JsonSerializer.SerializeToUtf8Bytes(document)));
        document["profiles"]!["pluggedIn"]!["lighting"]!.AsObject().Remove("controlsKeyboardEffect");
        document["schemaVersion"] = 27;
        Assert.Throws<InvalidDataException>(() => store.ReadDocument(JsonSerializer.SerializeToUtf8Bytes(document)));
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public async Task OverridesMigrateMissingOptInAndRoundTripNewIntent(int schema, bool enabled)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "overrides.json");
        var expected = new PersistentOverrideState(null, null,
            LightingSettingsFactory.SpectrumCycling(70) with { ControlsKeyboardEffect = enabled });
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(
            new { schemaVersion = schema, overrides = expected }, ProfileStore.SerializerOptions));
        PersistentOverrideLoadResult result = await new PersistentOverrideStore(path).LoadAsync(default);
        Assert.False(result.Recovered);
        Assert.Equal(expected, result.Overrides);
        Assert.Equal(9, JsonNode.Parse(await File.ReadAllTextAsync(path))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task OldOverrideOptInIsQuarantinedWithoutReinterpretation()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "overrides.json");
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 8,
            overrides = new PersistentOverrideState(null, null,
                LightingSettingsFactory.SpectrumCycling(70) with { ControlsKeyboardEffect = true }),
        }, ProfileStore.SerializerOptions);
        await File.WriteAllBytesAsync(path, original);
        var store = new PersistentOverrideStore(path);
        PersistentOverrideLoadResult result = await store.LoadAsync(default);
        Assert.True(result.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, result.Disposition);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() => store.SaveAsync(PersistentOverrideState.Empty, default));
    }

    [Fact]
    public async Task ConfigurationRoundTripsOptInAndMigratesLegacyUnsetWithoutOptingIn()
    {
        Directory.CreateDirectory(directory);
        string profilesPath = Path.Combine(directory, "profiles.json");
        string preferencesPath = Path.Combine(directory, "preferences.json");
        string markerPath = Path.Combine(directory, "configuration.json");
        var store = new BatteryConfigurationStore(profilesPath, preferencesPath, markerPath);
        BatteryConfiguration initial = await store.LoadAsync(default);
        LightingSettings baseline = initial.Profiles.PluggedIn.Lighting;
        await store.SaveConfigurationAsync(initial.Profiles,
            initial.Preferences with { LastKnownLighting = baseline with { ControlsKeyboardEffect = true } }, default);
        BatteryConfiguration current = await new BatteryConfigurationStore(profilesPath, preferencesPath, markerPath).LoadAsync(default);
        Assert.True(current.Preferences.LastKnownLighting!.ControlsKeyboardEffect);

        JsonNode profiles = JsonNode.Parse(await File.ReadAllTextAsync(profilesPath))!;
        JsonNode preferences = JsonNode.Parse(await File.ReadAllTextAsync(preferencesPath))!;
        profiles["schemaVersion"] = 25;
        preferences["schemaVersion"] = 25;
        preferences["preferences"]!["lastKnownLighting"]!.AsObject().Remove("controlsKeyboardEffect");
        byte[] profileBytes = JsonSerializer.SerializeToUtf8Bytes(profiles, ProfileStore.SerializerOptions);
        byte[] preferenceBytes = JsonSerializer.SerializeToUtf8Bytes(preferences, ProfileStore.SerializerOptions);
        await File.WriteAllBytesAsync(profilesPath, profileBytes);
        await File.WriteAllBytesAsync(preferencesPath, preferenceBytes);
        await File.WriteAllBytesAsync(markerPath, JsonSerializer.SerializeToUtf8Bytes(new BatteryConfigurationCommit(25,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profileBytes)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(preferenceBytes))), ProfileStore.SerializerOptions));
        BatteryConfiguration migrated = await new BatteryConfigurationStore(profilesPath, preferencesPath, markerPath).LoadAsync(default);
        Assert.False(migrated.Recovered);
        Assert.False(migrated.Preferences.LastKnownLighting!.ControlsKeyboardEffect);
        Assert.Equal(baseline, migrated.Preferences.LastKnownLighting);
        Assert.Equal(26, JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!["schemaVersion"]!.GetValue<int>());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
