using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record ProfileExportDocument(
    int SchemaVersion,
    string ApplicationVersion,
    DateTimeOffset ExportedAtUtc,
    ProfileSet Profiles);

public static class ProfileExportSerializer
{
    public const int CurrentSchemaVersion = 9;

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase,
                    allowIntegerValues: false),
            },
        };

    public static string Serialize(
        ProfileSet profiles,
        string applicationVersion,
        DateTimeOffset exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ValidateBatteryProtection(profiles);
        if (string.IsNullOrWhiteSpace(applicationVersion))
        {
            throw new ArgumentException(
                "An application version is required.",
                nameof(applicationVersion));
        }

        return JsonSerializer.Serialize(
            new ProfileExportDocument(
                CurrentSchemaVersion,
                applicationVersion,
                exportedAtUtc.ToUniversalTime(),
                profiles),
            SerializerOptions);
    }

    public static ProfileSet Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The profile file is empty.");
        }

        int schemaVersion = ReadSchemaVersion(json);
        return schemaVersion switch
        {
            1 => DeserializeVersion1(json),
            2 => DeserializeVersion2(json),
            3 => DeserializeVersion3(json),
            4 => DeserializeVersion4(json),
            5 => DeserializeVersion5(json),
            6 => DeserializeVersion6(json),
            7 => DeserializeVersion7(json),
            8 => DeserializeVersion8(json),
            9 => DeserializeVersion9(json),
            > CurrentSchemaVersion => throw new InvalidDataException(
                $"Profile schema {schemaVersion} was created by a newer version of OpenBlade."),
            _ => throw new InvalidDataException(
                $"Profile schema {schemaVersion} is not supported by this version of OpenBlade."),
        };
    }

    private static int ReadSchemaVersion(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "schemaVersion",
                    out JsonElement schemaVersionElement)
                || schemaVersionElement.ValueKind != JsonValueKind.Number
                || !schemaVersionElement.TryGetInt32(out int schemaVersion))
            {
                throw new InvalidDataException(
                    "The profile file does not contain a valid schema version.");
            }

            return schemaVersion;
        }
        catch (JsonException exception)
        {
            throw InvalidDocument(exception);
        }
    }

    // Keep each historical reader isolated. A future schema can deserialize into
    // its own DTO and migrate to ProfileSet before returning from Deserialize.
    private static ProfileSet DeserializeVersion1(string json)
    {
        return NormalizeLegacyProfile(DeserializeDocument(json).Profiles);
    }

    private static ProfileSet DeserializeVersion2(string json)
    {
        return NormalizeLegacyProfile(DeserializeDocument(json).Profiles);
    }

    private static ProfileSet DeserializeVersion3(string json)
    {
        return NormalizeLegacyProfile(DeserializeDocument(json).Profiles);
    }

    private static ProfileSet DeserializeVersion4(string json)
    {
        return NormalizeLegacyProfile(DeserializeDocument(json).Profiles);
    }

    private static ProfileSet DeserializeVersion5(string json)
    {
        return NormalizeLegacyProfile(DeserializeDocument(json).Profiles);
    }

    private static ProfileSet DeserializeVersion6(string json)
    {
        return NormalizeLegacyProfile(DeserializeDocument(json).Profiles);
    }

    private static ProfileSet DeserializeVersion7(string json)
    {
        ProfileSet profiles = ClearLegacyWindowsPowerModes(
            NormalizeLegacyGpuClockTuning(
                NormalizeAudioMeterDecay(DeserializeDocument(json).Profiles)));
        ValidateBatteryProtection(profiles);
        return profiles;
    }

    private static ProfileSet DeserializeVersion8(string json)
    {
        ProfileSet profiles = NormalizeLegacyGpuClockTuning(
            NormalizeAudioMeterDecay(DeserializeDocument(json).Profiles));
        ValidateBatteryProtection(profiles);
        return profiles;
    }

    private static ProfileSet DeserializeVersion9(string json)
    {
        ProfileSet profiles = NormalizeLegacyGpuClockTuning(
            NormalizeAudioMeterDecay(DeserializeDocument(json).Profiles));
        ValidateBatteryProtection(profiles);
        return profiles;
    }

    private static ProfileSet NormalizeLegacyProfile(ProfileSet profiles) =>
        ClearLegacyWindowsPowerModes(
            NormalizeLegacyBatteryProtection(
                NormalizeLegacyGpuClockTuning(NormalizeAudioMeterDecay(profiles))));

    private static ProfileSet ClearLegacyWindowsPowerModes(ProfileSet profiles) => profiles with
    {
        PluggedIn = profiles.PluggedIn with { WindowsPowerMode = null },
        Battery = profiles.Battery with { WindowsPowerMode = null },
        UsbC = profiles.UsbC with { WindowsPowerMode = null },
    };

    private static ProfileSet NormalizeLegacyBatteryProtection(ProfileSet profiles) =>
        profiles with
        {
            PluggedIn = profiles.PluggedIn with { BatteryProtectionEnabled = true },
            Battery = profiles.Battery with { BatteryProtectionEnabled = true },
            UsbC = profiles.UsbC with { BatteryProtectionEnabled = true },
        };

    private static void ValidateBatteryProtection(ProfileSet profiles)
    {
        if (!profiles.PluggedIn.BatteryProtectionEnabled.HasValue
            || !profiles.Battery.BatteryProtectionEnabled.HasValue
            || !profiles.UsbC.BatteryProtectionEnabled.HasValue)
        {
            throw new InvalidDataException(
                "The profile file is missing a battery protection setting.");
        }
    }

    private static ProfileSet NormalizeAudioMeterDecay(ProfileSet profiles) => profiles with
    {
        PluggedIn = NormalizeAudioMeterDecay(profiles.PluggedIn),
        Battery = NormalizeAudioMeterDecay(profiles.Battery),
        UsbC = NormalizeAudioMeterDecay(profiles.UsbC),
    };

    private static ProfileSet NormalizeLegacyGpuClockTuning(ProfileSet profiles) => profiles with
    {
        PluggedIn = NormalizeLegacyGpuClockTuning(profiles.PluggedIn),
    };

    private static PowerProfile NormalizeLegacyGpuClockTuning(PowerProfile profile)
    {
        if (profile.GpuClockTuning is not null
            || profile.Performance != PerformancePreset.Custom
            || profile.CustomPerformance?.GpuClockOffsets is not { } legacyOffsets)
        {
            return profile;
        }

        return profile with
        {
            CustomPerformance = profile.CustomPerformance with
            {
                GpuClockOffsets = null,
            },
            GpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets),
        };
    }

    private static PowerProfile NormalizeAudioMeterDecay(PowerProfile profile) => profile with
    {
        Lighting = LightingSettingsFactory.NormalizeAudioMeterDecay(profile.Lighting),
    };

    private static ProfileExportDocument DeserializeDocument(string json)
    {
        ProfileExportDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileExportDocument>(
                json,
                SerializerOptions)
                ?? throw new InvalidDataException("The profile file is empty.");
        }
        catch (JsonException exception)
        {
            throw InvalidDocument(exception);
        }

        if (document.Profiles is null)
        {
            throw new InvalidDataException("The profile file does not contain profiles.");
        }

        return document;
    }

    private static InvalidDataException InvalidDocument(Exception exception) =>
        new(
            "The selected file is not a valid OpenBlade profile export.",
            exception);
}
