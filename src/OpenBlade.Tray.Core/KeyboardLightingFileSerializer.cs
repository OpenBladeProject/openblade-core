using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record KeyboardLightingPreset(
    string Name,
    string? Author,
    string? Description,
    int Revision,
    LightingSettings Lighting);

public static class KeyboardLightingFileSerializer
{
    public const string FormatIdentifier = "openblade-keyboard-lighting";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileSizeBytes = 64 * 1024;
    public const int MaximumNameLength = 80;
    public const int MaximumAuthorLength = 80;
    public const int MaximumDescriptionLength = 500;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            Converters =
            {
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase,
                    allowIntegerValues: false),
            },
        };

    public static KeyboardLightingPreset Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            throw new InvalidDataException("The keyboard lighting file is empty.");
        }
        if (utf8Json.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException(
                $"Keyboard lighting files cannot exceed {MaximumFileSizeBytes / 1024} KiB.");
        }

        try
        {
            ReadOnlySpan<byte> content = utf8Json.StartsWith(Encoding.UTF8.Preamble)
                ? utf8Json[Encoding.UTF8.Preamble.Length..]
                : utf8Json;
            return Deserialize(StrictUtf8.GetString(content));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The keyboard lighting file must use UTF-8 encoding.",
                exception);
        }
    }

    public static KeyboardLightingPreset Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The keyboard lighting file is empty.");
        }
        if (StrictUtf8.GetByteCount(json) > MaximumFileSizeBytes)
        {
            throw new InvalidDataException(
                $"Keyboard lighting files cannot exceed {MaximumFileSizeBytes / 1024} KiB.");
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = SerializerOptions.MaxDepth,
                });
            RejectDuplicateProperties(parsed.RootElement);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The keyboard lighting file must contain one JSON object.");
            }

            string format = ReadRequiredString(parsed.RootElement, "format");
            if (!string.Equals(format, FormatIdentifier, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The file format must be '{FormatIdentifier}'.");
            }

            int schemaVersion = ReadSchemaVersion(parsed.RootElement);
            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Keyboard lighting schema {schemaVersion} was created by a newer version of OpenBlade.");
            }
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Keyboard lighting schema {schemaVersion} is not supported by this version of OpenBlade.");
            }

            KeyboardLightingFileDocument document = parsed.RootElement.Deserialize<
                KeyboardLightingFileDocument>(SerializerOptions)
                ?? throw new InvalidDataException("The keyboard lighting file is empty.");
            ValidateMetadata(document.Metadata);
            LightingSettings lighting = ToLightingSettings(document.Effect);
            if (!LightingSettingsValidation.IsWellFormed(lighting))
            {
                throw new InvalidDataException(
                    "The keyboard lighting effect parameters are invalid.");
            }

            return new KeyboardLightingPreset(
                document.Metadata.Name.Trim(),
                TrimOptional(document.Metadata.Author),
                TrimOptional(document.Metadata.Description),
                document.Metadata.Revision,
                lighting);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidDocument(exception);
        }
    }

    public static string Serialize(KeyboardLightingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ValidateMetadata(new KeyboardLightingFileMetadata(
            preset.Name,
            preset.Revision,
            preset.Author,
            preset.Description));
        if (!LightingSettingsValidation.IsWellFormed(preset.Lighting)
            || preset.Lighting.LogoMode is not null)
        {
            throw new ArgumentException(
                "The preset must contain well-formed keyboard lighting without a logo mode.",
                nameof(preset));
        }

        var document = new KeyboardLightingFileDocument(
            FormatIdentifier,
            CurrentSchemaVersion,
            new KeyboardLightingFileMetadata(
                preset.Name.Trim(),
                preset.Revision,
                TrimOptional(preset.Author),
                TrimOptional(preset.Description)),
            FromLightingSettings(preset.Lighting));
        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (!root.TryGetProperty("schemaVersion", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int schemaVersion))
        {
            throw new InvalidDataException(
                "The keyboard lighting file does not contain a valid schema version.");
        }

        return schemaVersion;
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text)
        {
            throw new InvalidDataException(
                $"The keyboard lighting file does not contain a valid {propertyName}.");
        }

        return text;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The keyboard lighting file contains duplicate '{property.Name}' properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void ValidateMetadata(KeyboardLightingFileMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateRequiredText(metadata.Name, MaximumNameLength, "name");
        ValidateOptionalText(metadata.Author, MaximumAuthorLength, "author");
        ValidateOptionalText(metadata.Description, MaximumDescriptionLength, "description");
        if (metadata.Revision <= 0)
        {
            throw new InvalidDataException("The keyboard lighting revision must be positive.");
        }
    }

    private static void ValidateRequiredText(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength)
        {
            throw new InvalidDataException(
                $"The keyboard lighting {field} must contain 1 to {maximumLength} characters.");
        }
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string field)
    {
        if (value is not null
            && (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength))
        {
            throw new InvalidDataException(
                $"The keyboard lighting {field} must contain 1 to {maximumLength} characters when present.");
        }
    }

    private static string? TrimOptional(string? value) => value?.Trim();

    private static LightingSettings ToLightingSettings(KeyboardLightingFileEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return effect switch
        {
            OffKeyboardLightingFileEffect => LightingSettingsFactory.Off(),
            StaticKeyboardLightingFileEffect value => LightingSettingsFactory.Static(
                ParseColor(value.Color),
                Brightness(value.BrightnessPercent)),
            SpectrumKeyboardLightingFileEffect value =>
                LightingSettingsFactory.SpectrumCycling(Brightness(value.BrightnessPercent)),
            BreathingKeyboardLightingFileEffect value => Breathing(value),
            ReactiveKeyboardLightingFileEffect value => LightingSettingsFactory.Reactive(
                ParseColor(value.Color),
                Brightness(value.BrightnessPercent),
                value.Duration),
            TidalKeyboardLightingFileEffect value => LightingSettingsFactory.Tidal(
                ParseColor(value.PrimaryColor),
                ParseColor(value.SecondaryColor),
                value.RandomColors,
                value.Direction,
                Brightness(value.BrightnessPercent)),
            StarlightKeyboardLightingFileEffect value => Starlight(value),
            WaveKeyboardLightingFileEffect value => LightingSettingsFactory.Wave(
                value.Direction,
                Brightness(value.BrightnessPercent)),
            WheelKeyboardLightingFileEffect value => LightingSettingsFactory.Wheel(
                value.Direction,
                Brightness(value.BrightnessPercent)),
            RippleKeyboardLightingFileEffect value => LightingSettingsFactory.Ripple(
                ParseColor(value.Color),
                Brightness(value.BrightnessPercent)),
            AmbientAwarenessKeyboardLightingFileEffect value =>
                LightingSettingsFactory.AmbientAwareness(
                    value.Region,
                    Brightness(value.BrightnessPercent)),
            AudioMeterKeyboardLightingFileEffect value => LightingSettingsFactory.AudioMeter(
                value.ColorBoost,
                Brightness(value.BrightnessPercent),
                value.DecayMilliseconds),
            FireKeyboardLightingFileEffect value =>
                LightingSettingsFactory.Fire(Brightness(value.BrightnessPercent)),
            _ => throw new InvalidDataException(
                "The keyboard lighting file contains an unsupported effect."),
        };
    }

    private static LightingSettings Breathing(BreathingKeyboardLightingFileEffect value) =>
        value.ColorMode switch
        {
            BreathingColorMode.Single when value.PrimaryColor is not null
                && value.SecondaryColor is null => LightingSettingsFactory.BreathingSingle(
                    ParseColor(value.PrimaryColor),
                    Brightness(value.BrightnessPercent)),
            BreathingColorMode.Dual when value.PrimaryColor is not null
                && value.SecondaryColor is not null => LightingSettingsFactory.BreathingDual(
                    ParseColor(value.PrimaryColor),
                    ParseColor(value.SecondaryColor),
                    Brightness(value.BrightnessPercent)),
            BreathingColorMode.Random when value.PrimaryColor is null
                && value.SecondaryColor is null => LightingSettingsFactory.BreathingRandom(
                    Brightness(value.BrightnessPercent)),
            _ => throw new InvalidDataException(
                "Breathing color fields do not match the selected color mode."),
        };

    private static LightingSettings Starlight(StarlightKeyboardLightingFileEffect value) =>
        value.ColorMode switch
        {
            StarlightColorMode.Single when value.PrimaryColor is not null
                && value.SecondaryColor is null => LightingSettingsFactory.StarlightSingle(
                    ParseColor(value.PrimaryColor),
                    Brightness(value.BrightnessPercent),
                    value.Duration),
            StarlightColorMode.Dual when value.PrimaryColor is not null
                && value.SecondaryColor is not null => LightingSettingsFactory.StarlightDual(
                    ParseColor(value.PrimaryColor),
                    ParseColor(value.SecondaryColor),
                    Brightness(value.BrightnessPercent),
                    value.Duration),
            StarlightColorMode.Random when value.PrimaryColor is null
                && value.SecondaryColor is null => LightingSettingsFactory.StarlightRandom(
                    Brightness(value.BrightnessPercent),
                    value.Duration),
            _ => throw new InvalidDataException(
                "Starlight color fields do not match the selected color mode."),
        };

    private static byte Brightness(int value)
    {
        if (value is < 1 or > 100)
        {
            throw new InvalidDataException(
                "Keyboard lighting brightnessPercent must be from 1 to 100.");
        }
        return checked((byte)value);
    }

    private static RgbColor ParseColor(string value)
    {
        if (value is not { Length: 7 } || value[0] != '#'
            || !byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber,
                provider: null, out byte red)
            || !byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber,
                provider: null, out byte green)
            || !byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber,
                provider: null, out byte blue))
        {
            throw new InvalidDataException(
                $"'{value}' is not a valid #RRGGBB color.");
        }
        return new RgbColor(red, green, blue);
    }

    private static KeyboardLightingFileEffect FromLightingSettings(LightingSettings settings) =>
        (settings.Effect, settings.Parameters) switch
        {
            (LightingEffect.Off, OffLightingParameters) =>
                new OffKeyboardLightingFileEffect(),
            (LightingEffect.Static, StaticLightingParameters value) =>
                new StaticKeyboardLightingFileEffect(settings.Brightness, Color(value.Color)),
            (LightingEffect.Spectrum, SpectrumCyclingLightingParameters) =>
                new SpectrumKeyboardLightingFileEffect(settings.Brightness),
            (LightingEffect.Breathing, BreathingLightingParameters value) =>
                new BreathingKeyboardLightingFileEffect(
                    settings.Brightness,
                    value.ColorMode,
                    value.PrimaryColor is { } primary ? Color(primary) : null,
                    value.SecondaryColor is { } secondary ? Color(secondary) : null),
            (LightingEffect.Reactive, ReactiveLightingParameters value) =>
                new ReactiveKeyboardLightingFileEffect(
                    settings.Brightness,
                    Color(value.Color),
                    value.Duration),
            (LightingEffect.Tidal, TidalLightingParameters value) =>
                new TidalKeyboardLightingFileEffect(
                    settings.Brightness,
                    Color(value.PrimaryColor),
                    Color(value.SecondaryColor),
                    value.RandomColors,
                    value.Direction),
            (LightingEffect.Starlight, StarlightLightingParameters value) =>
                new StarlightKeyboardLightingFileEffect(
                    settings.Brightness,
                    value.ColorMode,
                    value.PrimaryColor is { } primary ? Color(primary) : null,
                    value.SecondaryColor is { } secondary ? Color(secondary) : null,
                    value.Duration),
            (LightingEffect.Wave, WaveLightingParameters value) =>
                new WaveKeyboardLightingFileEffect(settings.Brightness, value.Direction),
            (LightingEffect.Wheel, WheelLightingParameters value) =>
                new WheelKeyboardLightingFileEffect(settings.Brightness, value.Direction),
            (LightingEffect.Ripple, RippleLightingParameters value) =>
                new RippleKeyboardLightingFileEffect(settings.Brightness, Color(value.Color)),
            (LightingEffect.AmbientAwareness, AmbientAwarenessLightingParameters value) =>
                new AmbientAwarenessKeyboardLightingFileEffect(
                    settings.Brightness,
                    value.Region),
            (LightingEffect.AudioMeter, AudioMeterLightingParameters value) =>
                new AudioMeterKeyboardLightingFileEffect(
                    settings.Brightness,
                    value.ColorBoost,
                    value.DecayMilliseconds),
            (LightingEffect.Fire, FireLightingParameters) =>
                new FireKeyboardLightingFileEffect(settings.Brightness),
            _ => throw new ArgumentException(
                "The preset contains unsupported keyboard lighting parameters.",
                nameof(settings)),
        };

    private static string Color(RgbColor value) =>
        $"#{value.Red:X2}{value.Green:X2}{value.Blue:X2}";

    private static InvalidDataException InvalidDocument(Exception exception) =>
        new("The selected file is not a valid OpenBlade keyboard lighting file.", exception);
}

internal sealed record KeyboardLightingFileDocument(
    string Format,
    int SchemaVersion,
    KeyboardLightingFileMetadata Metadata,
    KeyboardLightingFileEffect Effect);

internal sealed record KeyboardLightingFileMetadata(
    string Name,
    int Revision,
    string? Author = null,
    string? Description = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OffKeyboardLightingFileEffect), "off")]
[JsonDerivedType(typeof(StaticKeyboardLightingFileEffect), "static")]
[JsonDerivedType(typeof(SpectrumKeyboardLightingFileEffect), "spectrumCycling")]
[JsonDerivedType(typeof(BreathingKeyboardLightingFileEffect), "breathing")]
[JsonDerivedType(typeof(ReactiveKeyboardLightingFileEffect), "reactive")]
[JsonDerivedType(typeof(TidalKeyboardLightingFileEffect), "tidal")]
[JsonDerivedType(typeof(StarlightKeyboardLightingFileEffect), "starlight")]
[JsonDerivedType(typeof(WaveKeyboardLightingFileEffect), "wave")]
[JsonDerivedType(typeof(WheelKeyboardLightingFileEffect), "wheel")]
[JsonDerivedType(typeof(RippleKeyboardLightingFileEffect), "ripple")]
[JsonDerivedType(typeof(AmbientAwarenessKeyboardLightingFileEffect), "ambientAwareness")]
[JsonDerivedType(typeof(AudioMeterKeyboardLightingFileEffect), "audioMeter")]
[JsonDerivedType(typeof(FireKeyboardLightingFileEffect), "fire")]
internal abstract record KeyboardLightingFileEffect;

internal sealed record OffKeyboardLightingFileEffect : KeyboardLightingFileEffect;

internal sealed record StaticKeyboardLightingFileEffect(
    int BrightnessPercent,
    string Color) : KeyboardLightingFileEffect;

internal sealed record SpectrumKeyboardLightingFileEffect(
    int BrightnessPercent) : KeyboardLightingFileEffect;

internal sealed record BreathingKeyboardLightingFileEffect(
    int BrightnessPercent,
    BreathingColorMode ColorMode,
    string? PrimaryColor,
    string? SecondaryColor) : KeyboardLightingFileEffect;

internal sealed record ReactiveKeyboardLightingFileEffect(
    int BrightnessPercent,
    string Color,
    ReactiveLightingDuration Duration) : KeyboardLightingFileEffect;

internal sealed record TidalKeyboardLightingFileEffect(
    int BrightnessPercent,
    string PrimaryColor,
    string SecondaryColor,
    bool RandomColors,
    LightingDirection Direction) : KeyboardLightingFileEffect;

internal sealed record StarlightKeyboardLightingFileEffect(
    int BrightnessPercent,
    StarlightColorMode ColorMode,
    string? PrimaryColor,
    string? SecondaryColor,
    StarlightLightingDuration Duration) : KeyboardLightingFileEffect;

internal sealed record WaveKeyboardLightingFileEffect(
    int BrightnessPercent,
    LightingDirection Direction) : KeyboardLightingFileEffect;

internal sealed record WheelKeyboardLightingFileEffect(
    int BrightnessPercent,
    LightingRotationDirection Direction) : KeyboardLightingFileEffect;

internal sealed record RippleKeyboardLightingFileEffect(
    int BrightnessPercent,
    string Color) : KeyboardLightingFileEffect;

internal sealed record AmbientAwarenessKeyboardLightingFileEffect(
    int BrightnessPercent,
    AmbientAwarenessRegion Region) : KeyboardLightingFileEffect;

internal sealed record AudioMeterKeyboardLightingFileEffect(
    int BrightnessPercent,
    double ColorBoost,
    int DecayMilliseconds) : KeyboardLightingFileEffect;

internal sealed record FireKeyboardLightingFileEffect(
    int BrightnessPercent) : KeyboardLightingFileEffect;
