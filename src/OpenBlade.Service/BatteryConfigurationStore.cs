using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class BatteryConfigurationStore
{
    private readonly ProfileStore profileStore;
    private readonly string preferencesPath;
    private readonly string markerPath;
    private readonly ProfileSetValidator validator;
    private readonly Func<CancellationToken, Task>? postCommitBoundary;
    private readonly SemaphoreSlim gate = new(1, 1);

    public BatteryConfigurationStore(
        string profilePath,
        string preferencesPath,
        string markerPath,
        ProfileSetValidator? validator = null)
        : this(profilePath, preferencesPath, markerPath, validator, null)
    {
    }

    internal BatteryConfigurationStore(
        string profilePath,
        string preferencesPath,
        string markerPath,
        ProfileSetValidator? validator,
        Func<CancellationToken, Task>? postCommitBoundary)
    {
        this.validator = validator ?? ProfileSetValidator.CreateUnverifiedSafeDefaults();
        profileStore = new ProfileStore(profilePath, this.validator);
        this.preferencesPath = preferencesPath
            ?? throw new ArgumentNullException(nameof(preferencesPath));
        this.markerPath = markerPath ?? throw new ArgumentNullException(nameof(markerPath));
        this.postCommitBoundary = postCommitBoundary;
    }

    public async Task<BatteryConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(profileStore.Path)
                && File.Exists(preferencesPath)
                && File.Exists(markerPath))
            {
                try
                {
                    return (await ReadCommittedSnapshotAsync(cancellationToken)).Configuration;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    // A committed aggregate must be accepted as a whole or not at all.
                }
            }

            bool anyConfigurationFile = File.Exists(profileStore.Path)
                || File.Exists(preferencesPath)
                || File.Exists(markerPath);
            BatteryConfiguration configuration;
            if (File.Exists(profileStore.Path)
                && await TryReadLegacyAsync(cancellationToken) is { } migrated)
            {
                configuration = new BatteryConfiguration(
                    migrated,
                    new BatteryAutomationPreferences(ProtectionEnabled: true),
                    Recovered: false);
            }
            else
            {
                configuration = SafeConfiguration(recovered: anyConfigurationFile);
            }

            return await CommitAndVerifyAsync(configuration, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveProfilesAsync(
        ProfileSet profiles,
        CancellationToken cancellationToken)
    {
        validator.Validate(profiles);
        await gate.WaitAsync(cancellationToken);
        try
        {
            CommittedSnapshot committed =
                await ReadCommittedSnapshotAsync(cancellationToken);
            byte[] profileJson = SerializeProfiles(profiles);
            await AtomicFile.WriteAsync(profileStore.Path, profileJson, cancellationToken);
            await WriteMarkerAsync(
                profileJson,
                committed.PreferenceJson,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveConfigurationAsync(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        CancellationToken cancellationToken)
    {
        validator.Validate(profiles);
        validator.ValidatePreferences(preferences);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await CommitAsync(profiles, preferences, cancellationToken);
            CommittedSnapshot verified = await ReadCommittedSnapshotAsync(cancellationToken);
            if (verified.Configuration.Profiles != profiles
                || verified.Configuration.Preferences != preferences)
            {
                throw new InvalidDataException(
                    "The committed battery configuration did not match the intended aggregate.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CommittedSnapshot> ReadCommittedSnapshotAsync(
        CancellationToken cancellationToken)
    {
        byte[] profileJson = await File.ReadAllBytesAsync(profileStore.Path, cancellationToken);
        byte[] preferenceJson = await File.ReadAllBytesAsync(preferencesPath, cancellationToken);
        byte[] markerJson = await File.ReadAllBytesAsync(markerPath, cancellationToken);

        RejectDuplicateMembers(markerJson);
        BatteryConfigurationCommit? marker = JsonSerializer.Deserialize<BatteryConfigurationCommit>(
            markerJson,
            ProfileStore.SerializerOptions);
        if (marker is null || marker.SchemaVersion != ProfileStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The battery configuration marker is unsupported.");
        }

        if (!FixedHashEquals(marker.ProfileSha256, Hash(profileJson))
            || !FixedHashEquals(marker.PreferencesSha256, Hash(preferenceJson)))
        {
            throw new InvalidDataException("The battery configuration hashes do not match.");
        }

        ProfileSet profiles = profileStore.ReadDocument(profileJson);
        BatteryAutomationPreferences preferences = ReadPreferences(preferenceJson);
        return new CommittedSnapshot(
            new BatteryConfiguration(profiles, preferences, Recovered: false),
            preferenceJson);
    }

    private async Task<ProfileSet?> TryReadLegacyAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] json = await File.ReadAllBytesAsync(profileStore.Path, cancellationToken);
            return ReadLegacy(json);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    private ProfileSet ReadLegacy(ReadOnlySpan<byte> json)
    {
        RejectDuplicateMembers(json);
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement root = document.RootElement;
        RequireObjectMembers(root, "pluggedIn", "battery", "batteryPolicy");
        PowerProfile pluggedIn = ReadLegacyProfile(root.GetProperty("pluggedIn"));
        PowerProfile battery = ReadLegacyProfile(root.GetProperty("battery"));
        JsonElement policy = root.GetProperty("batteryPolicy");
        RequireObjectMembers(policy, "chargeLimitPercent", "temporaryFullCharge");
        if (policy.GetProperty("temporaryFullCharge").ValueKind is not JsonValueKind.False)
        {
            throw new InvalidDataException("Legacy temporary full charge must be false.");
        }

        var profiles = new ProfileSet(
            pluggedIn,
            battery,
            BatteryPolicy.Create(ReadInt32(policy, "chargeLimitPercent")));
        validator.Validate(profiles);
        return profiles;
    }

    private static PowerProfile ReadLegacyProfile(JsonElement element)
    {
        RequireObjectMembers(element, "performance", "fans", "lighting");
        JsonElement lighting = element.GetProperty("lighting");
        RequireObjectMembers(lighting, "effect", "color", "brightness");
        JsonElement color = lighting.GetProperty("color");
        RequireObjectMembers(color, allowSubset: true, "red", "green", "blue");
        return new PowerProfile(
            (PerformancePreset)ReadInt32(element, "performance"),
            (FanMode)ReadInt32(element, "fans"),
            new LightingSettings(
                (LightingEffect)ReadInt32(lighting, "effect"),
                new RgbColor(
                    ReadByte(color, "red"),
                    ReadByte(color, "green"),
                    ReadByte(color, "blue")),
                checked((byte)ReadInt32(lighting, "brightness"))));
    }

    private BatteryAutomationPreferences ReadPreferences(ReadOnlySpan<byte> json)
    {
        RejectDuplicateMembers(json);
        BatteryPreferencesDocument? document =
            JsonSerializer.Deserialize<BatteryPreferencesDocument>(
                json,
                ProfileStore.SerializerOptions);
        if (document is null || document.SchemaVersion != ProfileStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The battery preference schema is unsupported.");
        }

        validator.ValidatePreferences(document.Preferences);
        return document.Preferences;
    }

    private async Task CommitAsync(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        CancellationToken cancellationToken)
    {
        validator.Validate(profiles);
        validator.ValidatePreferences(preferences);
        byte[] profileJson = SerializeProfiles(profiles);
        byte[] preferenceJson = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryPreferencesDocument(ProfileStore.CurrentSchemaVersion, preferences),
            ProfileStore.SerializerOptions);

        await AtomicFile.WriteAsync(profileStore.Path, profileJson, cancellationToken);
        await AtomicFile.WriteAsync(preferencesPath, preferenceJson, cancellationToken);
        await WriteMarkerAsync(profileJson, preferenceJson, cancellationToken);
    }

    private async Task<BatteryConfiguration> CommitAndVerifyAsync(
        BatteryConfiguration intended,
        CancellationToken cancellationToken)
    {
        await CommitAsync(intended.Profiles, intended.Preferences, cancellationToken);
        if (postCommitBoundary is not null)
        {
            await postCommitBoundary(cancellationToken);
        }

        try
        {
            CommittedSnapshot verified =
                await ReadCommittedSnapshotAsync(cancellationToken);
            return verified.Configuration with { Recovered = intended.Recovered };
        }
        catch (Exception exception)
            when (IsRecoverable(exception) && !intended.Recovered)
        {
            BatteryConfiguration recovered = SafeConfiguration(recovered: true);
            await CommitAsync(
                recovered.Profiles,
                recovered.Preferences,
                cancellationToken);
            if (postCommitBoundary is not null)
            {
                await postCommitBoundary(cancellationToken);
            }

            CommittedSnapshot verified =
                await ReadCommittedSnapshotAsync(cancellationToken);
            return verified.Configuration with { Recovered = true };
        }
    }

    private async Task WriteMarkerAsync(
        byte[] profileJson,
        byte[] preferenceJson,
        CancellationToken cancellationToken)
    {
        byte[] markerJson = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.CurrentSchemaVersion,
                Hash(profileJson),
                Hash(preferenceJson)),
            ProfileStore.SerializerOptions);
        await AtomicFile.WriteAsync(markerPath, markerJson, cancellationToken);
    }

    private static byte[] SerializeProfiles(ProfileSet profiles) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(ProfileStore.CurrentSchemaVersion, profiles),
            ProfileStore.SerializerOptions);

    private static BatteryConfiguration SafeConfiguration(bool recovered) => new(
        ProfileDefaults.CreateSafeDefaults(),
        new BatteryAutomationPreferences(ProtectionEnabled: true),
        recovered);

    private static byte ReadByte(JsonElement element, string name) =>
        element.TryGetProperty(name, out _)
            ? checked((byte)ReadInt32(element, name))
            : (byte)0;

    private static int ReadInt32(JsonElement element, string name)
    {
        JsonElement member = element.GetProperty(name);
        if (member.ValueKind is not JsonValueKind.Number
            || !member.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"The JSON member '{name}' must be an integer.");
        }

        return value;
    }

    private static void RequireObjectMembers(JsonElement element, params string[] expected) =>
        RequireObjectMembers(element, allowSubset: false, expected);

    private static void RequireObjectMembers(
        JsonElement element,
        bool allowSubset,
        params string[] expected)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("A required JSON object is missing.");
        }

        HashSet<string> actual = element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> allowed = expected.ToHashSet(StringComparer.Ordinal);
        if (!actual.IsSubsetOf(allowed) || (!allowSubset && !actual.SetEquals(allowed)))
        {
            throw new InvalidDataException("The JSON document has missing or unknown members.");
        }
    }

    internal static void RejectDuplicateMembers(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        RejectDuplicateMembers(ref reader);
    }

    private static void RejectDuplicateMembers(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("The JSON document is empty.");
        }

        InspectValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("The JSON document has trailing content.");
        }
    }

    private static void InspectValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName
                    || !names.Add(reader.GetString()!))
                {
                    throw new JsonException("The JSON document has a duplicate member.");
                }

                if (!reader.Read())
                {
                    throw new JsonException("The JSON document ended unexpectedly.");
                }

                InspectValue(ref reader);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                InspectValue(ref reader);
            }
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static bool FixedHashEquals(string? supplied, string expected)
    {
        if (supplied is null || supplied.Length != expected.Length
            || supplied.Any(character => character is >= 'a' and <= 'f'))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(supplied),
            System.Text.Encoding.ASCII.GetBytes(expected));
    }

    internal static bool IsRecoverable(Exception exception) => exception is
        JsonException or
        NotSupportedException or
        ArgumentException or
        InvalidDataException or
        OverflowException;

    private sealed record CommittedSnapshot(
        BatteryConfiguration Configuration,
        byte[] PreferenceJson);
}
