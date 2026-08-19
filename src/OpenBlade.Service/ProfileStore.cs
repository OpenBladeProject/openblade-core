using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record ProfileLoadResult(ProfileSet Profiles, bool RecoveredFromInvalidFile);

public sealed class ProfileStore(string path, ProfileSetValidator? validator = null)
{
    internal const int CurrentSchemaVersion = 15;
    internal const int LegacyWindowsPowerModeSchemaVersion = 14;
    internal const int LatestSupportedSchemaVersion = 13;
    internal const int PreviousSchemaVersion = 12;
    internal const int FormerSchemaVersion = 11;
    internal const int OlderSchemaVersion = 10;
    internal const int OldestSupportedSchemaVersion = 9;
    internal const int EarlierSupportedSchemaVersion = 8;
    internal const int EarliestSupportedSchemaVersion = 7;
    internal const int AncientSupportedSchemaVersion = 6;
    internal const int PrehistoricSupportedSchemaVersion = 5;
    internal const int ArchaicSupportedSchemaVersion = 4;
    internal const int AntiqueSupportedSchemaVersion = 3;
    internal const int ObsoleteSupportedSchemaVersion = 2;
    internal const int LegacySchemaVersion = 1;
    internal const int BatteryProtectionProfileSchemaVersion = 11;
    internal const int WindowsPowerModeProfileSchemaVersion = 12;
    internal const int PerformanceWindowsPowerModeProfileSchemaVersion = 15;

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
        profiles = NormalizeLegacyBatteryProfile(
            profiles,
            out _,
            batteryProtectionDefault: null);
        profiles = ClearLegacyWindowsPowerModes(profiles);
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
        out int schemaVersion) => ReadDocument(
            json,
            out migrated,
            out schemaVersion,
            legacyBatteryProtectionDefault: true);

    internal ProfileSet ReadDocument(
        ReadOnlySpan<byte> json,
        out bool migrated,
        out int schemaVersion,
        bool legacyBatteryProtectionDefault)
    {
        BatteryConfigurationStore.RejectDuplicateMembers(json);
        using JsonDocument parsed = JsonDocument.Parse(json.ToArray());
        if (!parsed.RootElement.TryGetProperty("schemaVersion", out JsonElement versionElement)
            || !versionElement.TryGetInt32(out schemaVersion))
        {
            throw new InvalidDataException("The profile schema version is unsupported.");
        }
        if (schemaVersion == CurrentSchemaVersion
            && (!parsed.RootElement.TryGetProperty("profiles", out JsonElement profilesElement)
                || !profilesElement.TryGetProperty("windowsPowerModes", out _)))
        {
            throw new InvalidDataException(
                "The profile document is missing Windows power mode associations.");
        }

        ProfileSet profiles;
        if (schemaVersion is CurrentSchemaVersion
            or LegacyWindowsPowerModeSchemaVersion
            or LatestSupportedSchemaVersion
            or PreviousSchemaVersion
            or FormerSchemaVersion
            or OlderSchemaVersion
            or OldestSupportedSchemaVersion
            or EarlierSupportedSchemaVersion
            or EarliestSupportedSchemaVersion
            or AncientSupportedSchemaVersion
            or PrehistoricSupportedSchemaVersion
            or ArchaicSupportedSchemaVersion
            or AntiqueSupportedSchemaVersion
            or ObsoleteSupportedSchemaVersion)
        {
            ProfileDocument? document = JsonSerializer.Deserialize<ProfileDocument>(
                json,
                SerializerOptions);
            if (document is null)
            {
                throw new InvalidDataException("The profile document is missing.");
            }

            bool? protectionDefault = schemaVersion >= BatteryProtectionProfileSchemaVersion
                ? null
                : legacyBatteryProtectionDefault;
            profiles = NormalizeLegacyBatteryProfile(
                document.Profiles,
                out migrated,
                protectionDefault);
            if (schemaVersion < WindowsPowerModeProfileSchemaVersion)
            {
                profiles = ClearLegacyWindowsPowerModes(profiles) with
                {
                    WindowsPowerModes = WindowsPowerModeAssociations.LeaveUnchanged,
                };
            }
            else if (schemaVersion < PerformanceWindowsPowerModeProfileSchemaVersion)
            {
                ValidateLegacyWindowsPowerModes(profiles);
                profiles = MigrateLegacyWindowsPowerModes(profiles);
            }
            else
            {
                ProfileSet normalized = ClearLegacyWindowsPowerModes(profiles);
                migrated |= normalized != profiles;
                profiles = normalized;
            }
            migrated |= schemaVersion != CurrentSchemaVersion;
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
                ProfileDefaults.CreateSafeDefaults().UsbC with
                {
                    BatteryProtectionEnabled = legacyBatteryProtectionDefault,
                },
                document.Profiles.BatteryPolicy);
            profiles = NormalizeLegacyBatteryProfile(
                migratedProfiles,
                out _,
                legacyBatteryProtectionDefault);
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
        out bool migrated,
        bool? batteryProtectionDefault)
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
        pluggedIn = NormalizeLegacyCustomPerformance(pluggedIn);
        pluggedIn = NormalizeLegacyGpuClockTuning(pluggedIn);
        LightingPowerPolicy legacyLightingPowerPolicy =
            profiles.LightingPowerPolicy ?? LightingPowerPolicy.Disabled;
        pluggedIn = NormalizeLightingPowerPolicy(pluggedIn, legacyLightingPowerPolicy);
        battery = NormalizeLightingPowerPolicy(battery, legacyLightingPowerPolicy);
        usbC = NormalizeLightingPowerPolicy(usbC, legacyLightingPowerPolicy);
        pluggedIn = NormalizeBatteryProtection(pluggedIn, batteryProtectionDefault);
        battery = NormalizeBatteryProtection(battery, batteryProtectionDefault);
        usbC = NormalizeBatteryProtection(usbC, batteryProtectionDefault);
        migrated = pluggedIn != profiles.PluggedIn
            || battery != profiles.Battery
            || usbC != profiles.UsbC
            || profiles.LightingPowerPolicy is not null;
        return migrated
            ? profiles with
            {
                PluggedIn = pluggedIn,
                Battery = battery,
                UsbC = usbC,
                LightingPowerPolicy = null,
            }
            : profiles;
    }

    private static PowerProfile NormalizeLegacyLighting(PowerProfile profile) =>
        profile with
        {
            Lighting = LegacyLightingVisibilityNormalizer.Normalize(profile.Lighting),
        };

    private static PowerProfile NormalizeLegacyDisplay(PowerProfile profile) =>
        profile.Display is null
            ? profile with { Display = DisplayProfileSettings.Unconfigured() }
            : profile;

    private static PowerProfile NormalizeLegacyCustomPerformance(PowerProfile profile) =>
        profile.CustomPerformance is null
            ? profile with { CustomPerformance = CustomPerformanceSettings.Default }
            : profile;

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

    private static PowerProfile NormalizeLightingPowerPolicy(
        PowerProfile profile,
        LightingPowerPolicy legacyPolicy) => profile.LightingPowerPolicy is null
            ? profile with { LightingPowerPolicy = legacyPolicy }
            : profile;

    private static PowerProfile NormalizeBatteryProtection(
        PowerProfile profile,
        bool? batteryProtectionDefault) =>
        profile.BatteryProtectionEnabled is null && batteryProtectionDefault.HasValue
            ? profile with
            {
                BatteryProtectionEnabled = batteryProtectionDefault.Value,
            }
            : profile;

    private static ProfileSet MigrateLegacyWindowsPowerModes(ProfileSet profiles)
    {
        WindowsPowerModeAssociations associations =
            WindowsPowerModeAssociations.LeaveUnchanged;
        PowerProfile[] legacyProfiles =
        [
            profiles.PluggedIn,
            profiles.Battery,
            profiles.UsbC,
        ];
        foreach (PerformancePreset performance in Enum.GetValues<PerformancePreset>())
        {
            PowerProfile[] matching = legacyProfiles
                .Where(profile => profile.Performance == performance)
                .ToArray();
            WindowsPowerMode? mode = matching.Length > 0
                && matching.All(profile => profile.WindowsPowerMode == matching[0].WindowsPowerMode)
                    ? matching[0].WindowsPowerMode
                    : null;
            associations = associations.WithPerformance(performance, mode);
        }

        return ClearLegacyWindowsPowerModes(profiles) with
        {
            WindowsPowerModes = associations,
        };
    }

    private static void ValidateLegacyWindowsPowerModes(ProfileSet profiles)
    {
        foreach ((string name, PowerProfile profile) in new[]
        {
            ("plugged-in", profiles.PluggedIn),
            ("battery", profiles.Battery),
            ("USB-C", profiles.UsbC),
        })
        {
            if (profile.WindowsPowerMode is { } mode && !Enum.IsDefined(mode))
            {
                throw new InvalidDataException(
                    $"The legacy {name} Windows power mode is undefined.");
            }
        }
    }

    private static ProfileSet ClearLegacyWindowsPowerModes(ProfileSet profiles) => profiles with
    {
        PluggedIn = profiles.PluggedIn with { WindowsPowerMode = null },
        Battery = profiles.Battery with { WindowsPowerMode = null },
        UsbC = profiles.UsbC with { WindowsPowerMode = null },
    };

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
