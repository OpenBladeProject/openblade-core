using System.Text.Json;
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
        Assert.Equal(6, root.GetProperty("schemaVersion").GetInt32());
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
        };
        string json = ProfileExportSerializer.Serialize(
            expected,
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow);

        ProfileSet actual = ProfileExportSerializer.Deserialize(json);

        Assert.Equal(expected, actual);
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
                "\"schemaVersion\": 6",
                "\"schemaVersion\": 5",
                StringComparison.Ordinal);

        ProfileSet imported = ProfileExportSerializer.Deserialize(json);

        Assert.Null(imported.PluggedIn.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            new GpuClockTuningSettings(Enabled: true, offsets),
            imported.PluggedIn.GpuClockTuning);
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedSchema()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 6",
                "\"schemaVersion\": 7",
                StringComparison.Ordinal);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProfileExportSerializer.Deserialize(json));

        Assert.Contains("schema 7", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("newer version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsObsoleteSchemaThroughVersionDispatcher()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 6",
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
                "\"schemaVersion\": 6",
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
