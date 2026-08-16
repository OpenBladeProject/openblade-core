using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class KeyboardLightingFileSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_RoundTripsEverySupportedEffect()
    {
        foreach (LightingSettings lighting in SupportedSettings())
        {
            var expected = new KeyboardLightingPreset(
                "Northern lights",
                "A user",
                "Portable lighting.",
                3,
                lighting);

            string json = KeyboardLightingFileSerializer.Serialize(expected);
            KeyboardLightingPreset actual = KeyboardLightingFileSerializer.Deserialize(json);

            Assert.Equal(expected, actual);
            using JsonDocument parsed = JsonDocument.Parse(json);
            Assert.Equal(
                KeyboardLightingFileSerializer.FormatIdentifier,
                parsed.RootElement.GetProperty("format").GetString());
            Assert.Equal(
                KeyboardLightingFileSerializer.CurrentSchemaVersion,
                parsed.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                ExpectedKind(lighting.Effect),
                parsed.RootElement.GetProperty("effect").GetProperty("kind").GetString());
        }
    }

    [Fact]
    public void Deserialize_ReadsThePublishedExample()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "examples",
            "northern-lights.oblight");

        KeyboardLightingPreset preset = KeyboardLightingFileSerializer.Deserialize(
            File.ReadAllBytes(path));

        Assert.Equal("Northern lights", preset.Name);
        Assert.Equal("OpenBlade", preset.Author);
        Assert.Equal(
            LightingSettingsFactory.BreathingDual(
                new RgbColor(0x47, 0xE1, 0x0C),
                new RgbColor(0x8A, 0x2B, 0xE2),
                70),
            preset.Lighting);
    }

    [Fact]
    public void Deserialize_ReadsThePublishedSlowFadeSweepExample()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "examples",
            "slow-fade-sweep.oblight");

        KeyboardLightingPreset preset = KeyboardLightingFileSerializer.Deserialize(
            File.ReadAllBytes(path));

        Assert.Equal("Slow fade sweep", preset.Name);
        Assert.Equal("OpenBlade", preset.Author);
        Assert.Equal(
            LightingSettingsFactory.Tidal(
                new RgbColor(0x38, 0xE8, 0xFF),
                new RgbColor(0x00, 0x15, 0x2A),
                randomColors: false,
                LightingDirection.Rightward,
                brightness: 75),
            preset.Lighting);
    }

    [Fact]
    public void Deserialize_AcceptsUtf8Bom()
    {
        byte[] content = Encoding.UTF8.GetBytes(ValidJson());
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. content];

        KeyboardLightingPreset preset = KeyboardLightingFileSerializer.Deserialize(bytes);

        Assert.Equal(LightingEffect.Static, preset.Lighting.Effect);
    }

    [Fact]
    public void Deserialize_RejectsInvalidUtf8()
    {
        byte[] bytes = [0x7B, 0x22, 0xFF, 0x22, 0x7D];

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => KeyboardLightingFileSerializer.Deserialize(bytes));

        Assert.Contains("UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsFutureSchemaClearly()
    {
        string json = ValidJson().Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 2",
            StringComparison.Ordinal);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => KeyboardLightingFileSerializer.Deserialize(json));

        Assert.Contains("schema 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("newer version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Deserialize_RejectsInvalidDocuments(string json, string expectedMessage)
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => KeyboardLightingFileSerializer.Deserialize(json));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsOversizedFilesBeforeParsing()
    {
        byte[] bytes = new byte[KeyboardLightingFileSerializer.MaximumFileSizeBytes + 1];

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => KeyboardLightingFileSerializer.Deserialize(bytes));

        Assert.Contains("64 KiB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RejectsLogoLightingBecauseTheFormatIsKeyboardOnly()
    {
        var preset = new KeyboardLightingPreset(
            "Logo leak",
            null,
            null,
            1,
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50) with
            {
                LogoMode = LogoLightingMode.Static,
            });

        Assert.Throws<ArgumentException>(() =>
            KeyboardLightingFileSerializer.Serialize(preset));
    }

    public static TheoryData<string, string> InvalidDocuments()
    {
        string valid = ValidJson();
        return new TheoryData<string, string>
        {
            { "", "empty" },
            { "[]", "one JSON object" },
            {
                valid.Replace(
                    "\"format\": \"openblade-keyboard-lighting\"",
                    "\"format\": \"other\"",
                    StringComparison.Ordinal),
                "file format"
            },
            {
                valid.Replace(
                    "\"format\": \"openblade-keyboard-lighting\"",
                    "\"format\": \"openblade-keyboard-lighting\", \"format\": \"openblade-keyboard-lighting\"",
                    StringComparison.Ordinal),
                "duplicate"
            },
            {
                valid.Replace(
                    "\"revision\": 1",
                    "\"revision\": 1, \"unexpected\": true",
                    StringComparison.Ordinal),
                "valid OpenBlade"
            },
            {
                valid.Replace(
                    "\"brightnessPercent\": 60",
                    "\"brightnessPercent\": 0",
                    StringComparison.Ordinal),
                "brightnessPercent"
            },
            {
                valid.Replace("#47E10C", "green", StringComparison.Ordinal),
                "#RRGGBB"
            },
            {
                valid.Replace("\"revision\": 1", "\"revision\": 0", StringComparison.Ordinal),
                "positive"
            },
            {
                """
                {
                  "format": "openblade-keyboard-lighting",
                  "schemaVersion": 1,
                  "metadata": { "name": "Bad breathing", "revision": 1 },
                  "effect": {
                    "kind": "breathing",
                    "brightnessPercent": 50,
                    "colorMode": "random",
                    "primaryColor": "#112233",
                    "secondaryColor": null
                  }
                }
                """,
                "color fields"
            },
        };
    }

    private static string ValidJson() =>
        """
        {
          "format": "openblade-keyboard-lighting",
          "schemaVersion": 1,
          "metadata": {
            "name": "Green",
            "author": null,
            "description": null,
            "revision": 1
          },
          "effect": {
            "kind": "static",
            "brightnessPercent": 60,
            "color": "#47E10C"
          }
        }
        """;

    private static IReadOnlyList<LightingSettings> SupportedSettings()
    {
        var green = new RgbColor(0x47, 0xE1, 0x0C);
        var purple = new RgbColor(0x8A, 0x2B, 0xE2);
        return
        [
            LightingSettingsFactory.Off(),
            LightingSettingsFactory.Static(green, 90),
            LightingSettingsFactory.SpectrumCycling(80),
            LightingSettingsFactory.BreathingSingle(green, 70),
            LightingSettingsFactory.BreathingDual(green, purple, 60),
            LightingSettingsFactory.BreathingRandom(50),
            LightingSettingsFactory.Reactive(green, 40, ReactiveLightingDuration.Long),
            LightingSettingsFactory.Tidal(
                green,
                purple,
                false,
                LightingDirection.Leftward,
                30),
            LightingSettingsFactory.StarlightSingle(
                green,
                25,
                StarlightLightingDuration.Short),
            LightingSettingsFactory.StarlightDual(
                green,
                purple,
                35,
                StarlightLightingDuration.Medium),
            LightingSettingsFactory.StarlightRandom(45, StarlightLightingDuration.Long),
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 55),
            LightingSettingsFactory.Wheel(
                LightingRotationDirection.Counterclockwise,
                65),
            LightingSettingsFactory.Ripple(green, 75),
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Top, 85),
            LightingSettingsFactory.AudioMeter(2.25, 95, 750),
            LightingSettingsFactory.Fire(100),
        ];
    }

    private static string ExpectedKind(LightingEffect effect) => effect switch
    {
        LightingEffect.Spectrum => "spectrumCycling",
        LightingEffect.AmbientAwareness => "ambientAwareness",
        LightingEffect.AudioMeter => "audioMeter",
        _ => JsonNamingPolicy.CamelCase.ConvertName(effect.ToString()),
    };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the OpenBlade repository root.");
    }
}
