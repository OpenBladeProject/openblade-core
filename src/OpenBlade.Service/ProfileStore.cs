using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record ProfileLoadResult(ProfileSet Profiles, bool RecoveredFromInvalidFile);

public sealed class ProfileStore(string path, ProfileSetValidator? validator = null)
{
    internal const int CurrentSchemaVersion = 2;
    internal const int LegacySchemaVersion = 1;

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private readonly ProfileSetValidator validator =
        validator ?? ProfileSetValidator.CreateUnverifiedSafeDefaults();

    internal string Path => path;

    public async Task SaveAsync(ProfileSet profiles, CancellationToken cancellationToken)
    {
        profiles = NormalizeLegacyBatteryProfile(profiles, out _);
        validator.Validate(profiles);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(CurrentSchemaVersion, profiles),
            SerializerOptions);
        await AtomicFile.WriteAsync(path, json, cancellationToken);
    }

    public async Task<ProfileLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ProfileLoadResult(ProfileDefaults.CreateSafeDefaults(), false);
        }

        try
        {
            byte[] json = await File.ReadAllBytesAsync(path, cancellationToken);
            ProfileSet profiles = ReadDocument(json, out bool migrated);
            if (migrated)
            {
                await SaveAsync(profiles, cancellationToken);
            }
            return new ProfileLoadResult(profiles, false);
        }
        catch (Exception exception) when (BatteryConfigurationStore.IsRecoverable(exception))
        {
            return await RecoverSafeDefaultsAsync(cancellationToken);
        }
    }

    internal ProfileSet ReadDocument(ReadOnlySpan<byte> json) =>
        ReadDocument(json, out _);

    internal ProfileSet ReadDocument(ReadOnlySpan<byte> json, out bool migrated)
    {
        ProfileSet profiles = ReadDocument(json, out migrated, out _);
        return profiles;
    }

    internal ProfileSet ReadDocument(
        ReadOnlySpan<byte> json,
        out bool migrated,
        out int schemaVersion)
    {
        BatteryConfigurationStore.RejectDuplicateMembers(json);
        using JsonDocument parsed = JsonDocument.Parse(json.ToArray());
        if (!parsed.RootElement.TryGetProperty("schemaVersion", out JsonElement versionElement)
            || !versionElement.TryGetInt32(out schemaVersion))
        {
            throw new InvalidDataException("The profile schema version is unsupported.");
        }

        ProfileSet profiles;
        if (schemaVersion == CurrentSchemaVersion)
        {
            ProfileDocument? document = JsonSerializer.Deserialize<ProfileDocument>(
                json,
                SerializerOptions);
            if (document is null)
            {
                throw new InvalidDataException("The profile document is missing.");
            }

            profiles = NormalizeLegacyBatteryProfile(document.Profiles, out migrated);
        }
        else if (schemaVersion == LegacySchemaVersion)
        {
            LegacyProfileDocument? document =
                JsonSerializer.Deserialize<LegacyProfileDocument>(json, SerializerOptions);
            if (document is null)
            {
                throw new InvalidDataException("The legacy profile document is missing.");
            }

            ProfileSet migratedProfiles = new(
                document.Profiles.PluggedIn,
                document.Profiles.Battery,
                ProfileDefaults.CreateSafeDefaults().UsbC,
                document.Profiles.BatteryPolicy);
            profiles = NormalizeLegacyBatteryProfile(migratedProfiles, out _);
            migrated = true;
        }
        else
        {
            throw new InvalidDataException("The profile schema version is unsupported.");
        }

        validator.Validate(profiles);
        return profiles;
    }

    internal static ProfileSet NormalizeLegacyBatteryProfile(
        ProfileSet profiles,
        out bool migrated)
    {
        PerformancePreset normalized = profiles.Battery.Performance switch
        {
            PerformancePreset.Silent => PerformancePreset.BatterySaver,
            PerformancePreset.Balanced => PerformancePreset.BatteryBalanced,
            _ => profiles.Battery.Performance,
        };
        PowerProfile pluggedIn = NormalizeLegacyLighting(profiles.PluggedIn);
        PowerProfile battery = NormalizeLegacyLighting(
            profiles.Battery with { Performance = normalized });
        PowerProfile usbC = NormalizeLegacyLighting(profiles.UsbC);
        pluggedIn = NormalizeLegacyDisplay(pluggedIn);
        battery = NormalizeLegacyDisplay(battery);
        usbC = NormalizeLegacyDisplay(usbC);
        migrated = pluggedIn != profiles.PluggedIn
            || battery != profiles.Battery
            || usbC != profiles.UsbC;
        return migrated
            ? profiles with { PluggedIn = pluggedIn, Battery = battery, UsbC = usbC }
            : profiles;
    }

    private static PowerProfile NormalizeLegacyLighting(PowerProfile profile) =>
        profile with
        {
            Lighting = LightingSettingsFactory.NormalizeLegacyOff(profile.Lighting),
        };

    private static PowerProfile NormalizeLegacyDisplay(PowerProfile profile) =>
        profile.Display is null
            ? profile with { Display = DisplayProfileSettings.Unconfigured() }
            : profile;

    private async Task<ProfileLoadResult> RecoverSafeDefaultsAsync(
        CancellationToken cancellationToken)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        await SaveAsync(defaults, cancellationToken);
        return new ProfileLoadResult(defaults, true);
    }
}

internal static class AtomicFile
{
    public static async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(contents, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}
