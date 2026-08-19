using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string storeDirectory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveThenLoad_RoundTripsProfiles()
    {
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileStore store = new(path);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet expected = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.High,
                    CustomPerformanceLevel.Medium),
                GpuClockTuning = new GpuClockTuningSettings(
                    Enabled: true,
                    new GpuClockOffsetSettings(125, 500)),
                LaptopBehavior = new LaptopBehaviorProfileSettings(
                    StartupAnimationEnabled: true,
                    FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
                    GamingModeEnabled: true)
                {
                    GamingModeKeyPolicy = new GamingModeKeyPolicy(
                        BlockWindowsKey: true,
                        BlockAltTab: true),
                },
            },
            WindowsPowerModes = defaults.WindowsPowerModes.WithPerformance(
                PerformancePreset.Custom,
                WindowsPowerMode.BestPerformance),
        };

        await store.SaveAsync(expected, CancellationToken.None);
        ProfileLoadResult result = await store.LoadAsync(CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(expected, result.Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Load_Schema11MigratesWindowsPowerModeToLeaveUnchanged()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet mislabeledLegacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                WindowsPowerMode = WindowsPowerMode.BestPerformance,
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(ProfileStore.FormerSchemaVersion, mislabeledLegacy),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Null(result.Profiles.PluggedIn.WindowsPowerMode);
        Assert.Equal(
            WindowsPowerModeAssociations.LeaveUnchanged,
            result.Profiles.WindowsPowerModes);
        Assert.True(result.Profiles.PluggedIn.BatteryProtectionEnabled);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            await File.ReadAllTextAsync(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_Schema12MigratesDistinctPresetAssociations()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet configured = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Performance,
                WindowsPowerMode = WindowsPowerMode.BestPerformance,
            },
            Battery = defaults.Battery with
            {
                WindowsPowerMode = WindowsPowerMode.BestPowerEfficiency,
            },
            UsbC = defaults.UsbC with
            {
                WindowsPowerMode = WindowsPowerMode.Balanced,
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(ProfileStore.PreviousSchemaVersion, configured),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(
            WindowsPowerMode.BestPerformance,
            result.Profiles.WindowsPowerModes.Performance);
        Assert.Equal(
            WindowsPowerMode.BestPowerEfficiency,
            result.Profiles.WindowsPowerModes.BatterySaver);
        Assert.Equal(
            WindowsPowerMode.Balanced,
            result.Profiles.WindowsPowerModes.Balanced);
        Assert.Null(result.Profiles.WindowsPowerModes.Silent);
        Assert.Null(result.Profiles.PluggedIn.WindowsPowerMode);
        Assert.Null(result.Profiles.Battery.WindowsPowerMode);
        Assert.Null(result.Profiles.UsbC.WindowsPowerMode);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            await File.ReadAllTextAsync(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_Schema14ConflictingSamePresetLeavesWindowsUnchanged()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet conflicting = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                WindowsPowerMode = WindowsPowerMode.BestPerformance,
            },
            UsbC = defaults.UsbC with
            {
                WindowsPowerMode = WindowsPowerMode.Balanced,
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(
                    ProfileStore.LegacyWindowsPowerModeSchemaVersion,
                    conflicting),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Null(result.Profiles.WindowsPowerModes.Balanced);
    }

    [Fact]
    public async Task Load_Schema14AgreeingSamePresetPreservesAssociation()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet agreeing = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                WindowsPowerMode = WindowsPowerMode.Balanced,
            },
            UsbC = defaults.UsbC with
            {
                WindowsPowerMode = WindowsPowerMode.Balanced,
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(
                    ProfileStore.LegacyWindowsPowerModeSchemaVersion,
                    agreeing),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(
            WindowsPowerMode.Balanced,
            result.Profiles.WindowsPowerModes.Balanced);
    }

    [Fact]
    public async Task Load_Schema14ConfiguredAndNullSamePresetLeavesWindowsUnchanged()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet mixed = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                WindowsPowerMode = WindowsPowerMode.BestPerformance,
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(
                    ProfileStore.LegacyWindowsPowerModeSchemaVersion,
                    mixed),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Null(result.Profiles.WindowsPowerModes.Balanced);
    }

    [Fact]
    public void ReadDocument_Schema14RejectsUndefinedLegacyWindowsPowerModeBeforeMigration()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet malformed = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                WindowsPowerMode = (WindowsPowerMode)999,
            },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(
                ProfileStore.LegacyWindowsPowerModeSchemaVersion,
                malformed),
            ProfileStore.SerializerOptions);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new ProfileStore("unused").ReadDocument(json));

        Assert.Contains("legacy plugged-in Windows power mode is undefined", exception.Message);
    }

    [Fact]
    public void ReadDocument_CurrentSchemaRejectsMissingWindowsPowerModes()
    {
        JsonObject document = JsonSerializer.SerializeToNode(
            new ProfileDocument(
                ProfileStore.CurrentSchemaVersion,
                ProfileDefaults.CreateSafeDefaults()),
            ProfileStore.SerializerOptions)!.AsObject();
        document["profiles"]!.AsObject().Remove("windowsPowerModes");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new ProfileStore("unused").ReadDocument(
                JsonSerializer.SerializeToUtf8Bytes(document)));

        Assert.Contains("missing Windows power mode associations", exception.Message);
    }

    [Fact]
    public async Task Load_PreviousSchemaDoesNotInventGpuClockOffsets()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(ProfileStore.PreviousSchemaVersion, defaults),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Null(result.Profiles.PluggedIn.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            GamingModeKeyPolicy.Disabled,
            result.Profiles.PluggedIn
                .EffectiveLaptopBehavior
                .EffectiveGamingModeKeyPolicy);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_PreviousSchemaMigratesCustomGpuClockOffsetsToProfileTuning()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var offsets = new GpuClockOffsetSettings(125, 500);
        ProfileSet legacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.High,
                    CustomPerformanceLevel.Medium)
                {
                    GpuClockOffsets = offsets,
                },
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(ProfileStore.PreviousSchemaVersion, legacy),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Null(result.Profiles.PluggedIn.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            new GpuClockTuningSettings(Enabled: true, offsets),
            result.Profiles.PluggedIn.GpuClockTuning);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            await File.ReadAllTextAsync(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_PreviousSchemaKeepsBalancedGpuClockOffsetsDormant()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var offsets = new GpuClockOffsetSettings(125, 500);
        ProfileSet legacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Balanced,
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.High,
                    CustomPerformanceLevel.Medium)
                {
                    GpuClockOffsets = offsets,
                },
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(ProfileStore.PreviousSchemaVersion, legacy),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(offsets, result.Profiles.PluggedIn.CustomPerformance?.GpuClockOffsets);
        Assert.Null(result.Profiles.PluggedIn.GpuClockTuning);
    }

    [Fact]
    public void NormalizeLegacyBatteryProfile_DoesNotMigrateGpuTuningOutsidePluggedIn()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var offsets = new GpuClockOffsetSettings(125, 500);
        var legacyCustom = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium)
        {
            GpuClockOffsets = offsets,
        };
        ProfileSet legacy = defaults with
        {
            Battery = defaults.Battery with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = legacyCustom,
            },
            UsbC = defaults.UsbC with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = legacyCustom,
            },
        };

        ProfileSet normalized = ProfileStore.NormalizeLegacyBatteryProfile(
            legacy,
            out _,
            batteryProtectionDefault: true);

        Assert.Null(normalized.Battery.GpuClockTuning);
        Assert.Equal(offsets, normalized.Battery.CustomPerformance?.GpuClockOffsets);
        Assert.Null(normalized.UsbC.GpuClockTuning);
        Assert.Equal(offsets, normalized.UsbC.CustomPerformance?.GpuClockOffsets);
    }

    [Fact]
    public async Task Load_CorruptJsonReturnsSafeDefaultsAndReportsRecovery()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        await File.WriteAllTextAsync(path, "{not-json", CancellationToken.None);
        ProfileStore store = new(path);

        ProfileLoadResult result = await store.LoadAsync(CancellationToken.None);

        Assert.True(result.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), result.Profiles);
        Assert.Equal(
            ProfileDefaults.CreateSafeDefaults(),
            (await store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Load_SemanticallyInvalidJsonAtomicallyRecoversSafeDefaults()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        string json = """
            {
              "pluggedIn": {
                "performance": 999,
                "fans": 0,
                "lighting": { "effect": 0, "color": { "red": 0, "green": 0, "blue": 0 }, "brightness": 0 }
              },
              "battery": {
                "performance": 0,
                "fans": 0,
                "lighting": { "effect": 0, "color": { "red": 0, "green": 0, "blue": 0 }, "brightness": 0 }
              },
              "batteryPolicy": { "chargeLimitPercent": 80, "temporaryFullCharge": false }
            }
            """;
        await File.WriteAllTextAsync(path, json, CancellationToken.None);
        ProfileStore store = new(path);

        ProfileLoadResult first = await store.LoadAsync(CancellationToken.None);
        ProfileLoadResult second = await store.LoadAsync(CancellationToken.None);

        Assert.True(first.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), first.Profiles);
        Assert.False(second.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), second.Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Save_InvalidProfilesThrowsBeforeReplacingExistingFile()
    {
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileStore store = new(path);
        ProfileSet valid = ProfileDefaults.CreateSafeDefaults();
        await store.SaveAsync(valid, CancellationToken.None);
        ProfileSet invalid = valid with
        {
            PluggedIn = valid.PluggedIn with { Fans = (FanMode)999 },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(invalid, CancellationToken.None));

        Assert.Equal(valid, (await store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Load_CaseVariantSchemaMemberRecoversInsteadOfBindingLoosely()
    {
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileStore store = new(path);
        await store.SaveAsync(ProfileDefaults.CreateSafeDefaults(), CancellationToken.None);
        string json = (await File.ReadAllTextAsync(path))
            .Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        ProfileLoadResult result = await store.LoadAsync(CancellationToken.None);

        Assert.True(result.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), result.Profiles);
    }

    [Fact]
    public async Task Load_CurrentSchemaLegacyOffNormalizesAndRewritesTypedParameters()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        LightingSettings legacyOff = new(LightingEffect.Off, default, Brightness: 0);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet legacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Lighting = legacyOff },
            Battery = defaults.Battery with { Lighting = legacyOff },
            UsbC = defaults.UsbC with { Lighting = legacyOff },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(ProfileStore.CurrentSchemaVersion, legacy),
            ProfileStore.SerializerOptions);
        await File.WriteAllBytesAsync(path, json);

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.IsType<OffLightingParameters>(result.Profiles.PluggedIn.Lighting.Parameters);
        Assert.IsType<OffLightingParameters>(result.Profiles.Battery.Lighting.Parameters);
        Assert.IsType<OffLightingParameters>(result.Profiles.UsbC.Lighting.Parameters);
        Assert.Contains("\"kind\": \"off\"", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_CurrentSchemaNonOffZeroBrightnessMigratesToVisibleSetting()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet legacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = LightingSettingsFactory.BreathingSingle(default, 0),
            },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(ProfileStore.CurrentSchemaVersion, legacy),
            ProfileStore.SerializerOptions);
        await File.WriteAllBytesAsync(path, json);

        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Breathing,
            },
        };
        ProfileLoadResult result = await new ProfileStore(
            path,
            ProfileSetValidator.CreateForLightingCapabilities(capabilities))
            .LoadAsync(CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(
            LegacyLightingVisibilityNormalizer.VisibleRecoveryBrightness,
            result.Profiles.PluggedIn.Lighting.Brightness);
        Assert.Equal(
            LightingSettingsFactory.DefaultVisibleColor,
            result.Profiles.PluggedIn.Lighting.Color);
    }

    [Fact]
    public async Task Load_InvalidAudioMeterDecayNormalizesAndRewritesCurrentSchema()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet stored = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = LightingSettingsFactory.AudioMeter(
                    colorBoost: 1,
                    brightness: 50,
                    decayMilliseconds: 375),
            },
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(ProfileStore.CurrentSchemaVersion, stored),
                ProfileStore.SerializerOptions));
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.AudioMeter,
            },
        };

        ProfileLoadResult result = await new ProfileStore(
            path,
            ProfileSetValidator.CreateForLightingCapabilities(capabilities))
            .LoadAsync(CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(
            500,
            Assert.IsType<AudioMeterLightingParameters>(
                result.Profiles.PluggedIn.Lighting.Parameters).DecayMilliseconds);
        string rewritten = await File.ReadAllTextAsync(path);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains("\"decayMilliseconds\": 500", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_PreviousSchemaWithoutAudioMeterDecayDefaultsToOff()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet stored = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = LightingSettingsFactory.AudioMeter(1, 50),
            },
        };
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(
            JsonSerializer.Serialize(
                new ProfileDocument(ProfileStore.PreviousSchemaVersion, stored),
                ProfileStore.SerializerOptions)));
        JsonObject profiles = Assert.IsType<JsonObject>(root["profiles"]);
        JsonObject pluggedIn = Assert.IsType<JsonObject>(profiles["pluggedIn"]);
        JsonObject lighting = Assert.IsType<JsonObject>(pluggedIn["lighting"]);
        JsonObject parameters = Assert.IsType<JsonObject>(lighting["parameters"]);
        Assert.True(parameters.Remove("decayMilliseconds"));
        await File.WriteAllTextAsync(path, root.ToJsonString());
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.AudioMeter,
            },
        };

        ProfileLoadResult result = await new ProfileStore(
            path,
            ProfileSetValidator.CreateForLightingCapabilities(capabilities))
            .LoadAsync(CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(
            0,
            Assert.IsType<AudioMeterLightingParameters>(
                result.Profiles.PluggedIn.Lighting.Parameters).DecayMilliseconds);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            await File.ReadAllTextAsync(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_CurrentSchemaWithoutDisplaySettingsNormalizesAndRewritesProfiles()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet legacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Display = null },
            Battery = defaults.Battery with { Display = null },
            UsbC = defaults.UsbC with { Display = null },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(ProfileStore.CurrentSchemaVersion, legacy),
            ProfileStore.SerializerOptions);
        await File.WriteAllBytesAsync(path, json);

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(DisplayProfileSettings.Unconfigured(), result.Profiles.PluggedIn.Display);
        Assert.Equal(DisplayProfileSettings.Unconfigured(), result.Profiles.Battery.Display);
        Assert.Equal(DisplayProfileSettings.Unconfigured(), result.Profiles.UsbC.Display);
        Assert.DoesNotContain("\"display\": null", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_SchemaFourGlobalLightingPolicyMigratesToEveryPowerProfile()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var policy = new LightingPowerPolicy(
            turnOffWhenDisplayTurnsOff: true,
            idleTimeoutMinutes: 12);
        ProfileSet legacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with { LightingPowerPolicy = null },
            UsbC = defaults.UsbC with { LightingPowerPolicy = null },
            Battery = defaults.Battery with { LightingPowerPolicy = null },
            LightingPowerPolicy = policy,
        };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProfileDocument(ProfileStore.PreviousSchemaVersion, legacy),
                ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(policy, result.Profiles.PluggedIn.EffectiveLightingPowerPolicy);
        Assert.Equal(policy, result.Profiles.UsbC.EffectiveLightingPowerPolicy);
        Assert.Equal(policy, result.Profiles.Battery.EffectiveLightingPowerPolicy);
        Assert.Null(result.Profiles.LightingPowerPolicy);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_SchemaOneAddsUsbCAndNormalizesMissingDisplaySettings()
    {
        Directory.CreateDirectory(storeDirectory);
        string path = Path.Combine(storeDirectory, "profiles.json");
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var legacy = new LegacyProfileDocument(
            ProfileStore.LegacySchemaVersion,
            new LegacyProfileSet(
                defaults.PluggedIn with { Display = null },
                defaults.Battery with
                {
                    Performance = PerformancePreset.Silent,
                    Display = null,
                },
                BatteryPolicy.Create(75)));
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(legacy, ProfileStore.SerializerOptions));

        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(
            CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(DisplayProfileSettings.Unconfigured(), result.Profiles.PluggedIn.Display);
        Assert.Equal(DisplayProfileSettings.Unconfigured(), result.Profiles.Battery.Display);
        Assert.Equal(PerformancePreset.BatterySaver, result.Profiles.Battery.Performance);
        Assert.Equal(defaults.UsbC, result.Profiles.UsbC);
        Assert.Equal(75, result.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Contains(
            $"\"schemaVersion\": {ProfileStore.CurrentSchemaVersion}",
            await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("\"display\": null", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(storeDirectory))
        {
            Directory.Delete(storeDirectory, recursive: true);
        }
    }
}
