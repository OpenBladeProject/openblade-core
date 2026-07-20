using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class PersistentOverrideStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.PersistentOverrideStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsTypedOverrides()
    {
        PersistentOverrideStore store = CreateStore();
        var expected = new PersistentOverrideState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            new LightingSettings(
                LightingEffect.Static,
                new RgbColor(12, 34, 56),
                Brightness: 70),
            FanControl: FanControlSettings.FromLegacy(FanMode.Maximum));

        await store.SaveAsync(expected, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(expected, loaded.Overrides);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsFixedFanSpeedDetails()
    {
        PersistentOverrideStore store = CreateStore();
        var fanControl = new FanControlSettings(
            FanMode.Fixed,
            2800,
            ManualFanCurve.Default);
        var expected = new PersistentOverrideState(
            null,
            FanMode.Fixed,
            null,
            FanControl: fanControl);

        await store.SaveAsync(expected, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(fanControl, loaded.Overrides.FanControl);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsCustomPerformanceLevels()
    {
        PersistentOverrideStore store = CreateStore();
        var expected = new PersistentOverrideState(
            PerformancePreset.Custom,
            null,
            null,
            new CustomPerformanceSettings(
                CustomPerformanceLevel.High,
                CustomPerformanceLevel.Medium));

        await store.SaveAsync(expected, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(expected, loaded.Overrides);
    }

    [Fact]
    public async Task Load_MigratesSchemaOneWithoutInventingCustomLevels()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "overrides": {
                "performance": 2,
                "fans": null,
                "lighting": null
              }
            }
            """);

        PersistentOverrideLoadResult loaded = await new PersistentOverrideStore(path)
            .LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(PerformancePreset.Performance, loaded.Overrides.Performance);
        Assert.Null(loaded.Overrides.CustomPerformance);
    }

    [Fact]
    public async Task Load_MigratesSchemaTwoFanModeToLegacyDetails()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 2,
              "overrides": {
                "performance": null,
                "fans": 1,
                "lighting": null,
                "customPerformance": null
              }
            }
            """);

        PersistentOverrideLoadResult loaded = await new PersistentOverrideStore(path)
            .LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(FanMode.Fixed, loaded.Overrides.Fans);
        Assert.Equal(
            FanControlSettings.FromLegacy(FanMode.Fixed),
            loaded.Overrides.FanControl);
    }

    [Fact]
    public async Task Load_InvalidLightingParametersRecoversToEmptyState()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 3,
              "overrides": {
                "performance": null,
                "fans": null,
                "lighting": {
                  "effect": 1,
                  "color": { "red": 1, "green": 2, "blue": 3 },
                  "brightness": 50,
                  "parameters": { "kind": "off" }
                },
                "customPerformance": null,
                "fanControl": null
              }
            }
            """);

        PersistentOverrideLoadResult loaded = await new PersistentOverrideStore(path)
            .LoadAsync(CancellationToken.None);

        Assert.True(loaded.Recovered);
        Assert.Equal(PersistentOverrideState.Empty, loaded.Overrides);
    }

    [Fact]
    public async Task Load_CorruptDocumentRecoversOnceToEmptyState()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{invalid-json");
        var store = new PersistentOverrideStore(path);

        PersistentOverrideLoadResult recovered = await store.LoadAsync(CancellationToken.None);
        PersistentOverrideLoadResult verified = await store.LoadAsync(CancellationToken.None);

        Assert.True(recovered.Recovered);
        Assert.Equal(PersistentOverrideState.Empty, recovered.Overrides);
        Assert.False(verified.Recovered);
        Assert.Equal(PersistentOverrideState.Empty, verified.Overrides);
    }

    [Fact]
    public async Task Save_WriteFailurePreservesPreviouslyCommittedState()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        var baseline = new PersistentOverrideState(
            PerformancePreset.Silent,
            null,
            null);
        await new PersistentOverrideStore(path).SaveAsync(baseline, CancellationToken.None);
        var failing = new PersistentOverrideStore(
            path,
            _ => Task.FromException(new IOException("disk unavailable")));

        await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync(
            baseline with { Performance = PerformancePreset.Performance },
            CancellationToken.None));

        PersistentOverrideLoadResult loaded =
            await new PersistentOverrideStore(path).LoadAsync(CancellationToken.None);
        Assert.Equal(baseline, loaded.Overrides);
    }

    [Fact]
    public async Task Save_LegacyOffNormalizesToTypedOff()
    {
        PersistentOverrideStore store = CreateStore();
        var legacy = new PersistentOverrideState(
            null,
            null,
            new LightingSettings(LightingEffect.Off, default, Brightness: 0));

        await store.SaveAsync(legacy, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.IsType<OffLightingParameters>(loaded.Overrides.Lighting?.Parameters);
    }

    [Fact]
    public async Task Save_NonOffZeroBrightnessMigratesToVisibleSetting()
    {
        PersistentOverrideStore store = CreateStore();
        var legacy = new PersistentOverrideState(
            null,
            null,
            LightingSettingsFactory.BreathingSingle(default, 0));

        await store.SaveAsync(legacy, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(
            LightingSettingsFactory.DefaultVisibleBrightness,
            loaded.Overrides.Lighting?.Brightness);
        Assert.Equal(
            LightingSettingsFactory.DefaultVisibleColor,
            loaded.Overrides.Lighting?.Color);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private PersistentOverrideStore CreateStore() => new(
        Path.Combine(directory, "manual-overrides.json"));
}
