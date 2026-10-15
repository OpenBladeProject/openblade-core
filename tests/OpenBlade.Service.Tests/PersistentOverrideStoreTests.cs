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
    public async Task SaveAndLoad_RoundTripsSequenceWithoutChangingEnvelopeSchema()
    {
        PersistentOverrideStore store = CreateStore();
        string timeline = KeyboardLightingSequenceCodec.Encode(
        [
            new KeyboardLightingSequenceFrame(
                100,
                new RgbColor[AmbientLightingFrame.SlotCount]),
        ]);
        var expected = new PersistentOverrideState(
            null,
            null,
            LightingSettingsFactory.Sequence(
                KeyboardLightingLayoutIds.Rz09_0581,
                timeline,
                loop: true,
                brightness: 75));

        await store.SaveAsync(expected, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(expected, loaded.Overrides);
        Assert.Contains(
            "\"schemaVersion\": 7",
            await File.ReadAllTextAsync(
                Path.Combine(directory, "manual-overrides.json")),
            StringComparison.Ordinal);
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
    public async Task SaveAndLoad_RoundTripsIndependentGpuClockTuning()
    {
        PersistentOverrideStore store = CreateStore();
        var expected = new PersistentOverrideState(
            PerformancePreset.Custom,
            null,
            null,
            new CustomPerformanceSettings(
                CustomPerformanceLevel.High,
                CustomPerformanceLevel.Medium),
            GpuClockTuning: new GpuClockTuningSettings(
                Enabled: true,
                new GpuClockOffsetSettings(125, 500)));

        await store.SaveAsync(expected, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(expected, loaded.Overrides);
    }

    [Fact]
    public async Task Load_SchemaFiveMigratesCustomGpuClockOffsetsToIndependentTuning()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 5,
              "overrides": {
                "performance": 5,
                "fans": null,
                "lighting": null,
                "customPerformance": {
                  "cpu": 2,
                  "gpu": 1,
                  "gpuClockOffsets": {
                    "coreMegahertz": 125,
                    "memoryMegahertz": 500
                  }
                },
                "fanControl": null
              }
            }
            """);

        PersistentOverrideLoadResult loaded = await new PersistentOverrideStore(path)
            .LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Null(loaded.Overrides.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            new GpuClockTuningSettings(
                Enabled: true,
                new GpuClockOffsetSettings(125, 500)),
            loaded.Overrides.GpuClockTuning);
        Assert.Contains(
            "\"schemaVersion\": 7",
            await File.ReadAllTextAsync(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_SchemaThreeDoesNotInventGpuClockOffsets()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 3,
              "overrides": {
                "performance": 5,
                "fans": null,
                "lighting": null,
                "customPerformance": {
                  "cpu": 2,
                  "gpu": 1
                },
                "fanControl": null
              }
            }
            """);

        PersistentOverrideLoadResult loaded = await new PersistentOverrideStore(path)
            .LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Null(loaded.Overrides.CustomPerformance?.GpuClockOffsets);
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
    public async Task Load_CorruptDocumentRemainsBytePreservedInQuarantine()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{invalid-json");
        byte[] original = await File.ReadAllBytesAsync(path);
        var store = new PersistentOverrideStore(path);

        PersistentOverrideLoadResult recovered = await store.LoadAsync(CancellationToken.None);
        PersistentOverrideLoadResult verified = await store.LoadAsync(CancellationToken.None);

        Assert.True(recovered.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, recovered.Disposition);
        Assert.Equal(PersistentOverrideState.Empty, recovered.Overrides);
        Assert.True(verified.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, verified.Disposition);
        Assert.Equal(PersistentOverrideState.Empty, verified.Overrides);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task Load_NonObjectRootRemainsBytePreservedInQuarantine(string content)
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content);
        byte[] original = await File.ReadAllBytesAsync(path);
        var store = new PersistentOverrideStore(path);

        PersistentOverrideLoadResult recovered = await store.LoadAsync(CancellationToken.None);
        PersistentOverrideLoadResult verified = await store.LoadAsync(CancellationToken.None);

        Assert.True(recovered.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, recovered.Disposition);
        Assert.Equal(ConfigurationQuarantineReason.InvalidDocument, recovered.QuarantineReason);
        Assert.Equal(PersistentOverrideState.Empty, recovered.Overrides);
        Assert.True(verified.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, verified.Disposition);
        Assert.Equal(PersistentOverrideState.Empty, verified.Overrides);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Save_NonObjectRootRejectsSaveAsQuarantined(string content)
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content);
        byte[] original = await File.ReadAllBytesAsync(path);
        var store = new PersistentOverrideStore(path);
        var overrides = new PersistentOverrideState(
            PerformancePreset.Silent,
            null,
            null);

        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.SaveAsync(overrides, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Load_FutureSchemaRemainsBytePreservedAndRejectsEverySave()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        var overrides = new PersistentOverrideState(
            PerformancePreset.Silent,
            null,
            null);
        PersistentOverrideStore store = new(path);
        await store.SaveAsync(overrides, CancellationToken.None);
        string future = (await File.ReadAllTextAsync(path))
            .Replace(
                "\"schemaVersion\": 7",
                "\"schemaVersion\": 8, \"schemaVersion\": 7",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, future);
        byte[] preserved = await File.ReadAllBytesAsync(path);
        DateTime writeTime = File.GetLastWriteTimeUtc(path);

        PersistentOverrideLoadResult loaded = await store.LoadAsync(
            CancellationToken.None);
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.SaveAsync(overrides, CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            new PersistentOverrideStore(path).SaveAsync(
                overrides,
                CancellationToken.None));

        Assert.Equal(ConfigurationLoadDisposition.Quarantined, loaded.Disposition);
        Assert.Equal(ConfigurationQuarantineReason.FutureSchema, loaded.QuarantineReason);
        Assert.Equal(preserved, await File.ReadAllBytesAsync(path));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal([path], Directory.GetFiles(directory));
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
            LegacyLightingVisibilityNormalizer.VisibleRecoveryBrightness,
            loaded.Overrides.Lighting?.Brightness);
        Assert.Equal(
            LightingSettingsFactory.DefaultVisibleColor,
            loaded.Overrides.Lighting?.Color);
    }

    [Fact]
    public async Task Load_CurrentSchemaInvalidAudioMeterDecayNormalizesAndRewrites()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 6,
              "overrides": {
                "performance": null,
                "fans": null,
                "lighting": {
                  "effect": 11,
                  "color": { "red": 0, "green": 0, "blue": 0 },
                  "brightness": 50,
                  "parameters": {
                    "kind": "audioMeter",
                    "colorBoost": 1,
                    "decayMilliseconds": 375
                  }
                },
                "customPerformance": null,
                "fanControl": null
              }
            }
            """);

        PersistentOverrideLoadResult loaded = await new PersistentOverrideStore(path)
            .LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(
            500,
            Assert.IsType<AudioMeterLightingParameters>(
                loaded.Overrides.Lighting?.Parameters).DecayMilliseconds);
        Assert.Contains(
            "\"decayMilliseconds\": 500",
            await File.ReadAllTextAsync(path),
            StringComparison.Ordinal);
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
