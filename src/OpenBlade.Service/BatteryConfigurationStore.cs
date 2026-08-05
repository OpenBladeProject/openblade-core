using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum BatteryConfigurationCommitBoundary
{
    BeforeProfileReplacement,
    AfterProfileReplacement,
    BeforePreferencesReplacement,
    AfterPreferencesReplacement,
    BeforeMarkerReplacement,
    AfterMarkerReplacement,
    BeforeVerification,
    AfterVerification,
}

public sealed class BatteryConfigurationStore
{
    private readonly ProfileStore profileStore;
    private readonly string preferencesPath;
    private readonly string markerPath;
    private readonly ProfileSetValidator validator;
    private readonly Func<CancellationToken, Task>? postCommitBoundary;
    private readonly Func<BatteryConfigurationCommitBoundary, CancellationToken, Task>?
        transactionBoundary;
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
        : this(
            profilePath,
            preferencesPath,
            markerPath,
            validator,
            postCommitBoundary,
            transactionBoundary: null)
    {
    }

    internal BatteryConfigurationStore(
        string profilePath,
        string preferencesPath,
        string markerPath,
        ProfileSetValidator? validator,
        Func<CancellationToken, Task>? postCommitBoundary,
        Func<BatteryConfigurationCommitBoundary, CancellationToken, Task>?
            transactionBoundary)
    {
        this.validator = validator ?? ProfileSetValidator.CreateUnverifiedSafeDefaults();
        profileStore = new ProfileStore(profilePath, this.validator);
        this.preferencesPath = preferencesPath
            ?? throw new ArgumentNullException(nameof(preferencesPath));
        this.markerPath = markerPath ?? throw new ArgumentNullException(nameof(markerPath));
        this.postCommitBoundary = postCommitBoundary;
        this.transactionBoundary = transactionBoundary;
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
                    CommittedSnapshot committed =
                        await ReadCommittedSnapshotAsync(cancellationToken);
                    return committed.ProfileMigrated
                        ? await CommitAndVerifyAsync(
                            committed.Configuration,
                            cancellationToken)
                        : committed.Configuration;
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
        profiles = ProfileStore.NormalizeLegacyBatteryProfile(profiles, out _);
        validator.Validate(profiles);
        await gate.WaitAsync(cancellationToken);
        try
        {
            CommittedSnapshot committed =
                await ReadCommittedSnapshotAsync(cancellationToken);
            await SaveConfigurationTransactionAsync(
                profiles,
                committed.Configuration.Preferences,
                cancellationToken,
                committed.ProfileMigrated ? null : committed.PreferenceJson);
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
        profiles = ProfileStore.NormalizeLegacyBatteryProfile(profiles, out _);
        validator.Validate(profiles);
        validator.ValidatePreferences(preferences);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SaveConfigurationTransactionAsync(
                profiles,
                preferences,
                cancellationToken);
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
        if (marker is null || marker.SchemaVersion is not (
                ProfileStore.LegacySchemaVersion
                or ProfileStore.EarliestSupportedSchemaVersion
                or ProfileStore.EarlierSupportedSchemaVersion
                or ProfileStore.OldestSupportedSchemaVersion
                or ProfileStore.OlderSchemaVersion
                or ProfileStore.PreviousSchemaVersion
                or ProfileStore.AncientSupportedSchemaVersion
                or ProfileStore.PrehistoricSupportedSchemaVersion
                or ProfileStore.CurrentSchemaVersion))
        {
            throw new InvalidDataException("The battery configuration marker is unsupported.");
        }

        if (!FixedHashEquals(marker.ProfileSha256, Hash(profileJson))
            || !FixedHashEquals(marker.PreferencesSha256, Hash(preferenceJson)))
        {
            throw new InvalidDataException("The battery configuration hashes do not match.");
        }

        ProfileSet profiles = profileStore.ReadDocument(
            profileJson,
            out bool profileMigrated,
            out int profileSchemaVersion);
        if (profileSchemaVersion != marker.SchemaVersion)
        {
            throw new InvalidDataException(
                "The profile and battery configuration marker schemas do not match.");
        }

        BatteryAutomationPreferences preferences = ReadPreferences(
            preferenceJson,
            marker.SchemaVersion);
        return new CommittedSnapshot(
            new BatteryConfiguration(profiles, preferences, Recovered: false),
            profileJson,
            preferenceJson,
            markerJson,
            profileMigrated);
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

        ProfileSet profiles = new(
            pluggedIn,
            battery,
            ProfileDefaults.CreateSafeDefaults().UsbC,
            BatteryPolicy.Create(ReadInt32(policy, "chargeLimitPercent")));
        profiles = ProfileStore.NormalizeLegacyBatteryProfile(profiles, out _);
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

    private BatteryAutomationPreferences ReadPreferences(
        ReadOnlySpan<byte> json,
        int expectedSchemaVersion)
    {
        RejectDuplicateMembers(json);
        BatteryPreferencesDocument? document =
            JsonSerializer.Deserialize<BatteryPreferencesDocument>(
                json,
                ProfileStore.SerializerOptions);
        if (document is null || document.SchemaVersion != expectedSchemaVersion)
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

    private async Task SaveConfigurationTransactionAsync(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        CancellationToken cancellationToken,
        byte[]? committedPreferenceJson = null)
    {
        byte[] profileJson = SerializeProfiles(profiles);
        byte[] preferenceJson = committedPreferenceJson ??
            JsonSerializer.SerializeToUtf8Bytes(
                new BatteryPreferencesDocument(ProfileStore.CurrentSchemaVersion, preferences),
                ProfileStore.SerializerOptions);
        byte[] markerJson = SerializeMarker(profileJson, preferenceJson);
        TargetSnapshot profilePrior = await CaptureAsync(
            profileStore.Path,
            cancellationToken);
        TargetSnapshot preferencePrior = await CaptureAsync(
            preferencesPath,
            cancellationToken);
        TargetSnapshot markerPrior = await CaptureAsync(markerPath, cancellationToken);
        string transactionId = Guid.NewGuid().ToString("N");
        TransactionTarget profile = CreateTransactionTarget(
            profilePrior,
            profileJson,
            transactionId);
        TransactionTarget preference = CreateTransactionTarget(
            preferencePrior,
            preferenceJson,
            transactionId);
        TransactionTarget marker = CreateTransactionTarget(
            markerPrior,
            markerJson,
            transactionId);
        TransactionTarget[] targets = [profile, preference, marker];
        bool replacementPhaseStarted = false;

        try
        {
            foreach (TransactionTarget target in targets)
            {
                await WriteDurableFileAsync(
                    target.StagePath,
                    target.Intended,
                    cancellationToken);
                if (target.Prior.Exists)
                {
                    await WriteDurableFileAsync(
                        target.BackupPath,
                        target.Prior.Contents,
                        cancellationToken);
                }
            }

            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.BeforeProfileReplacement,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            replacementPhaseStarted = true;
            ReplaceStaged(profile);
            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.AfterProfileReplacement,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.BeforePreferencesReplacement,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceStaged(preference);
            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.AfterPreferencesReplacement,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.BeforeMarkerReplacement,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceStaged(marker);

            // Marker replacement is the commit point. From here onward caller
            // cancellation cannot turn an already committed intent into a reported
            // failure; verification and boundary probes are deliberately non-cancelable.
            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.AfterMarkerReplacement,
                CancellationToken.None);
            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.BeforeVerification,
                CancellationToken.None);
            await VerifyIntendedAggregateAsync(
                profiles,
                preferences,
                profileJson,
                preferenceJson,
                markerJson);
            await AtBoundaryAsync(
                BatteryConfigurationCommitBoundary.AfterVerification,
                CancellationToken.None);
        }
        catch (Exception commitFailure)
        {
            if (replacementPhaseStarted)
            {
                try
                {
                    await RestorePriorAggregateAsync(targets);
                }
                catch (Exception rollbackFailure)
                {
                    throw new IOException(
                        "Battery configuration commit and rollback both failed.",
                        new AggregateException(commitFailure, rollbackFailure));
                }
            }

            throw;
        }
        finally
        {
            foreach (TransactionTarget target in targets)
            {
                DeleteBestEffort(target.StagePath);
                DeleteBestEffort(target.BackupPath);
                DeleteBestEffort(target.RestorePath);
            }
        }
    }

    private async Task VerifyIntendedAggregateAsync(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        byte[] profileJson,
        byte[] preferenceJson,
        byte[] markerJson)
    {
        CommittedSnapshot verified = await ReadCommittedSnapshotAsync(CancellationToken.None);
        if (verified.Configuration.Profiles != profiles
            || verified.Configuration.Preferences != preferences
            || !verified.ProfileJson.AsSpan().SequenceEqual(profileJson)
            || !verified.PreferenceJson.AsSpan().SequenceEqual(preferenceJson)
            || !verified.MarkerJson.AsSpan().SequenceEqual(markerJson))
        {
            throw new InvalidDataException(
                "The committed battery configuration did not match the intended aggregate.");
        }
    }

    private async Task RestorePriorAggregateAsync(TransactionTarget[] targets)
    {
        // Data files are restored first and the marker last, mirroring the forward
        // commit order so the old aggregate becomes authoritative only when complete.
        foreach (TransactionTarget target in targets)
        {
            if (target.Prior.Exists)
            {
                byte[] backup = await File.ReadAllBytesAsync(
                    target.BackupPath,
                    CancellationToken.None);
                if (!backup.AsSpan().SequenceEqual(target.Prior.Contents))
                {
                    throw new InvalidDataException(
                        $"Rollback backup for '{target.Prior.Path}' was not exact.");
                }

                await WriteDurableFileAsync(
                    target.RestorePath,
                    backup,
                    CancellationToken.None);
                File.Move(target.RestorePath, target.Prior.Path, overwrite: true);
            }
            else
            {
                File.Delete(target.Prior.Path);
            }
        }

        foreach (TransactionTarget target in targets)
        {
            TargetSnapshot restored = await CaptureAsync(
                target.Prior.Path,
                CancellationToken.None);
            if (restored.Exists != target.Prior.Exists
                || !restored.Contents.AsSpan().SequenceEqual(target.Prior.Contents))
            {
                throw new InvalidDataException(
                    $"Rollback did not restore '{target.Prior.Path}' exactly.");
            }
        }

        if (targets.All(target => target.Prior.Exists))
        {
            _ = await ReadCommittedSnapshotAsync(CancellationToken.None);
        }
    }

    private Task AtBoundaryAsync(
        BatteryConfigurationCommitBoundary boundary,
        CancellationToken cancellationToken) => transactionBoundary is null
            ? Task.CompletedTask
            : transactionBoundary(boundary, cancellationToken);

    private static TransactionTarget CreateTransactionTarget(
        TargetSnapshot prior,
        byte[] intended,
        string transactionId) => new(
            prior,
            intended,
            $"{prior.Path}.transaction-{transactionId}.stage",
            $"{prior.Path}.transaction-{transactionId}.backup",
            $"{prior.Path}.transaction-{transactionId}.restore");

    private static async Task<TargetSnapshot> CaptureAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new TargetSnapshot(path, Exists: false, []);
        }

        return new TargetSnapshot(
            path,
            Exists: true,
            await File.ReadAllBytesAsync(path, cancellationToken));
    }

    private static async Task WriteDurableFileAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceStaged(TransactionTarget target) =>
        File.Move(target.StagePath, target.Prior.Path, overwrite: true);

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Transaction artifacts are non-authoritative. Leaving one behind is safer
            // than changing the success/failure result after commit or rollback.
        }
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
        byte[] markerJson = SerializeMarker(profileJson, preferenceJson);
        await AtomicFile.WriteAsync(markerPath, markerJson, cancellationToken);
    }

    private static byte[] SerializeMarker(byte[] profileJson, byte[] preferenceJson) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.CurrentSchemaVersion,
                Hash(profileJson),
                Hash(preferenceJson)),
            ProfileStore.SerializerOptions);

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

    private sealed record TargetSnapshot(string Path, bool Exists, byte[] Contents);

    private sealed record TransactionTarget(
        TargetSnapshot Prior,
        byte[] Intended,
        string StagePath,
        string BackupPath,
        string RestorePath);

    private sealed record CommittedSnapshot(
        BatteryConfiguration Configuration,
        byte[] ProfileJson,
        byte[] PreferenceJson,
        byte[] MarkerJson,
        bool ProfileMigrated);
}
