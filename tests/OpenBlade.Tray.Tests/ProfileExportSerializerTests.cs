using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ProfileExportSerializerTests
{
    [Fact]
    public void Serialize_WritesReadableVersionedJsonWithEveryProfile()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        DateTimeOffset exportedAt = new(2026, 7, 20, 9, 30, 0, TimeSpan.Zero);

        string json = ProfileExportSerializer.Serialize(
            profiles,
            "0.1.0+abcdef0",
            exportedAt);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(10, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("0.1.0+abcdef0", root.GetProperty("applicationVersion").GetString());
        Assert.Equal(exportedAt, root.GetProperty("exportedAtUtc").GetDateTimeOffset());
        JsonElement exportedProfiles = root.GetProperty("profiles");
        Assert.Equal(
            "balanced",
            exportedProfiles.GetProperty("pluggedIn").GetProperty("performance").GetString());
        Assert.Equal(
            "batterySaver",
            exportedProfiles.GetProperty("battery").GetProperty("performance").GetString());
        Assert.Equal(
            "balanced",
            exportedProfiles.GetProperty("usbC").GetProperty("performance").GetString());
        Assert.Equal(
            80,
            exportedProfiles.GetProperty("batteryPolicy")
                .GetProperty("chargeLimitPercent")
                .GetInt32());
        Assert.True(exportedProfiles.GetProperty("pluggedIn")
            .GetProperty("batteryProtectionEnabled").GetBoolean());
        Assert.True(exportedProfiles.GetProperty("battery")
            .GetProperty("batteryProtectionEnabled").GetBoolean());
        Assert.True(exportedProfiles.GetProperty("usbC")
            .GetProperty("batteryProtectionEnabled").GetBoolean());
        Assert.False(exportedProfiles.GetProperty("pluggedIn")
            .TryGetProperty("windowsPowerMode", out _));
        Assert.Equal(
            JsonValueKind.Null,
            exportedProfiles.GetProperty("windowsPowerModes")
                .GetProperty("balanced").ValueKind);
    }

    [Fact]
    public void Serialize_RejectsMissingVersion()
    {
        Assert.Throws<ArgumentException>(() => ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            " ",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Deserialize_RoundTripsEveryProfile()
    {
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
                    GamingModeEnabled: false)
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
        string json = ProfileExportSerializer.Serialize(
            expected,
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow);

        ProfileSet actual = ProfileExportSerializer.Deserialize(json);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Deserialize_VersionSevenPreservesBatteryProtectionAndLeavesPowerModeUnmanaged()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                BatteryProtectionEnabled = false,
            },
            Battery = profiles.Battery with
            {
                BatteryProtectionEnabled = true,
                WindowsPowerMode = WindowsPowerMode.BestPowerEfficiency,
            },
            UsbC = profiles.UsbC with
            {
                BatteryProtectionEnabled = false,
            },
        };
        string legacyJson = ProfileExportSerializer.Serialize(
            profiles,
            "0.5.6",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 10",
                "\"schemaVersion\": 7",
                StringComparison.Ordinal);

        ProfileSet imported = ProfileExportSerializer.Deserialize(legacyJson);

        Assert.False(imported.PluggedIn.BatteryProtectionEnabled);
        Assert.True(imported.Battery.BatteryProtectionEnabled);
        Assert.False(imported.UsbC.BatteryProtectionEnabled);
        Assert.Null(imported.Battery.WindowsPowerMode);
        Assert.Equal(
            WindowsPowerModeAssociations.LeaveUnchanged,
            imported.WindowsPowerModes);
    }

    [Fact]
    public void Deserialize_VersionNineMigratesPresetWindowsPowerModes()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet legacy = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Performance,
            },
        };
        JsonObject document = JsonNode.Parse(ProfileExportSerializer.Serialize(
            legacy,
            "0.14.2",
            DateTimeOffset.UtcNow))!.AsObject();
        document["schemaVersion"] = 9;
        JsonObject profiles = document["profiles"]!.AsObject();
        profiles.Remove("windowsPowerModes");
        profiles["pluggedIn"]!["windowsPowerMode"] = "bestPerformance";
        profiles["battery"]!["windowsPowerMode"] = "bestPowerEfficiency";
        profiles["usbC"]!["windowsPowerMode"] = "balanced";

        ProfileSet imported = ProfileExportSerializer.Deserialize(document.ToJsonString());

        Assert.Equal(WindowsPowerMode.BestPerformance,
            imported.WindowsPowerModes.Performance);
        Assert.Equal(WindowsPowerMode.BestPowerEfficiency,
            imported.WindowsPowerModes.BatterySaver);
        Assert.Equal(WindowsPowerMode.Balanced,
            imported.WindowsPowerModes.Balanced);
        Assert.Null(imported.WindowsPowerModes.Silent);
        Assert.Null(imported.PluggedIn.WindowsPowerMode);
    }

    [Fact]
    public void Deserialize_VersionNinePreservesAgreeingSamePresetAssociation()
    {
        JsonObject document = CreateVersionNineDocument();
        JsonObject profiles = document["profiles"]!.AsObject();
        profiles["pluggedIn"]!["windowsPowerMode"] = "balanced";
        profiles["usbC"]!["windowsPowerMode"] = "balanced";

        ProfileSet imported = ProfileExportSerializer.Deserialize(document.ToJsonString());

        Assert.Equal(WindowsPowerMode.Balanced, imported.WindowsPowerModes.Balanced);
    }

    [Fact]
    public void Deserialize_VersionNineConflictingSamePresetLeavesWindowsUnchanged()
    {
        JsonObject document = CreateVersionNineDocument();
        JsonObject profiles = document["profiles"]!.AsObject();
        profiles["pluggedIn"]!["windowsPowerMode"] = "bestPerformance";
        profiles["usbC"]!["windowsPowerMode"] = "balanced";

        ProfileSet imported = ProfileExportSerializer.Deserialize(document.ToJsonString());

        Assert.Null(imported.WindowsPowerModes.Balanced);
    }

    [Fact]
    public void Deserialize_VersionNineConfiguredAndNullSamePresetLeavesWindowsUnchanged()
    {
        JsonObject document = CreateVersionNineDocument();
        JsonObject profiles = document["profiles"]!.AsObject();
        profiles["pluggedIn"]!["windowsPowerMode"] = "bestPerformance";

        ProfileSet imported = ProfileExportSerializer.Deserialize(document.ToJsonString());

        Assert.Null(imported.WindowsPowerModes.Balanced);
    }

    [Fact]
    public void Deserialize_VersionNineRejectsNumericLegacyWindowsPowerMode()
    {
        JsonObject document = CreateVersionNineDocument();
        document["profiles"]!["pluggedIn"]!["windowsPowerMode"] = 999;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(document.ToJsonString()));

        Assert.Contains("not a valid OpenBlade profile export", exception.Message);
    }

    [Fact]
    public void Deserialize_VersionTenRejectsMissingWindowsPowerModes()
    {
        JsonObject document = JsonNode.Parse(ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.15.0",
            DateTimeOffset.UtcNow))!.AsObject();
        document["profiles"]!.AsObject().Remove("windowsPowerModes");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(document.ToJsonString()));

        Assert.Contains("missing Windows power mode associations", exception.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"invalid\"")]
    public void Deserialize_VersionTenRejectsNonObjectProfiles(string profilesJson)
    {
        string json = $$"""
            {
              "schemaVersion": 10,
              "applicationVersion": "0.16.0",
              "exportedAtUtc": "2026-08-20T00:00:00+00:00",
              "profiles": {{profilesJson}}
            }
            """;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(json));

        Assert.Contains("missing Windows power mode associations", exception.Message);
    }

    private static JsonObject CreateVersionNineDocument()
    {
        JsonObject document = JsonNode.Parse(ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.14.2",
            DateTimeOffset.UtcNow))!.AsObject();
        document["schemaVersion"] = 9;
        document["profiles"]!.AsObject().Remove("windowsPowerModes");
        return document;
    }

    [Fact]
    public void Deserialize_VersionFiveMigratesCustomGpuClockOffsets()
    {
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
        string json = ProfileExportSerializer.Serialize(
            legacy,
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 10",
                "\"schemaVersion\": 5",
                StringComparison.Ordinal);

        ProfileSet imported = ProfileExportSerializer.Deserialize(json);

        Assert.Null(imported.PluggedIn.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            new GpuClockTuningSettings(Enabled: true, offsets),
            imported.PluggedIn.GpuClockTuning);
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced, false)]
    [InlineData(PerformancePreset.Balanced, true)]
    [InlineData(PerformancePreset.Custom, true)]
    public void Deserialize_PreservesDormantLegacyOffsetsAndExplicitGpuTuning(
        PerformancePreset performance,
        bool hasExplicitTuning)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var legacyOffsets = new GpuClockOffsetSettings(125, 500);
        GpuClockTuningSettings? explicitTuning = hasExplicitTuning
            ? new GpuClockTuningSettings(false, new GpuClockOffsetSettings(50, 100))
            : null;
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = performance,
                CustomPerformance = CustomPerformanceSettings.Default with
                {
                    GpuClockOffsets = legacyOffsets,
                },
                GpuClockTuning = explicitTuning,
            },
        };
        string json = ProfileExportSerializer.Serialize(
            profiles,
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow);

        ProfileSet imported = ProfileExportSerializer.Deserialize(json);

        Assert.Equal(explicitTuning, imported.PluggedIn.GpuClockTuning);
        Assert.Equal(legacyOffsets, imported.PluggedIn.CustomPerformance?.GpuClockOffsets);
    }

    [Fact]
    public void Deserialize_VersionFiveDoesNotMigrateGpuTuningOutsidePluggedIn()
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
        string json = ProfileExportSerializer.Serialize(
            legacy,
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 10",
                "\"schemaVersion\": 5",
                StringComparison.Ordinal);

        ProfileSet imported = ProfileExportSerializer.Deserialize(json);

        Assert.Null(imported.Battery.GpuClockTuning);
        Assert.Equal(offsets, imported.Battery.CustomPerformance?.GpuClockOffsets);
        Assert.Null(imported.UsbC.GpuClockTuning);
        Assert.Equal(offsets, imported.UsbC.CustomPerformance?.GpuClockOffsets);
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedSchema()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 10",
                "\"schemaVersion\": 11",
                StringComparison.Ordinal);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProfileExportSerializer.Deserialize(json));

        Assert.Contains("schema 11", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("newer version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsObsoleteSchemaThroughVersionDispatcher()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 10",
                "\"schemaVersion\": 0",
                StringComparison.Ordinal);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProfileExportSerializer.Deserialize(json));

        Assert.Contains("schema 0", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_Version1WithoutLaptopBehaviorMigratesAsUnconfigured()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 10",
                "\"schemaVersion\": 1",
                StringComparison.Ordinal);

        ProfileSet imported = ProfileExportSerializer.Deserialize(json);

        Assert.False(imported.PluggedIn.EffectiveLaptopBehavior.IsConfigured);
        Assert.False(imported.UsbC.EffectiveLaptopBehavior.IsConfigured);
        Assert.False(imported.Battery.EffectiveLaptopBehavior.IsConfigured);
    }

    [Fact]
    public void Deserialize_NormalizesInvalidAudioMeterDecay()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = LightingSettingsFactory.AudioMeter(1, 50, 375),
            },
        };
        string json = ProfileExportSerializer.Serialize(
            profiles,
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow);

        ProfileSet imported = ProfileExportSerializer.Deserialize(json);

        Assert.Equal(
            500,
            Assert.IsType<AudioMeterLightingParameters>(
                imported.PluggedIn.Lighting.Parameters).DecayMilliseconds);
    }

    [Fact]
    public void Deserialize_RejectsNumericEnumValues()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"performance\": \"balanced\"",
                "\"performance\": 999",
                StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"profiles\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"unexpected\":true}")]
    public void Deserialize_RejectsInvalidDocuments(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(json));
    }
}
