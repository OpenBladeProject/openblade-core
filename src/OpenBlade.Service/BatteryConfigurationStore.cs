using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, Lazy<SemaphoreSlim>> AggregateGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ProfileStore profileStore;
    private readonly string preferencesPath;
    private readonly string markerPath;
    private readonly ProfileSetValidator validator;
    private readonly Func<CancellationToken, Task>? postCommitBoundary;
    private readonly Func<BatteryConfigurationCommitBoundary, CancellationToken, Task>?
        transactionBoundary;
    private readonly SemaphoreSlim gate;
    private bool preservationRequired;

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
        string fullProfilePath = Path.GetFullPath(
            profilePath ?? throw new ArgumentNullException(nameof(profilePath)));
        this.preferencesPath = Path.GetFullPath(
            preferencesPath ?? throw new ArgumentNullException(nameof(preferencesPath)));
        this.markerPath = Path.GetFullPath(
            markerPath ?? throw new ArgumentNullException(nameof(markerPath)));
        profileStore = new ProfileStore(fullProfilePath, this.validator);
        string aggregateKey = string.Join(
            "|",
            fullProfilePath,
            this.preferencesPath,
            this.markerPath);
        gate = AggregateGates.GetOrAdd(
            aggregateKey,
            static _ => new Lazy<SemaphoreSlim>(
                static () => new SemaphoreSlim(1, 1),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        this.postCommitBoundary = postCommitBoundary;
        this.transactionBoundary = transactionBoundary;
    }

    public async Task<BatteryConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            BatteryConfigurationInspection inspection = await LoadCoreAsync(
                materializeAdmittedState: true,
                cancellationToken);
            preservationRequired = !inspection.Configuration.IsAuthoritative;
            return inspection.Configuration;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<BatteryConfigurationInspection> InspectAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(
                    materializeAdmittedState: false,
                    cancellationToken)
                .ConfigureAwait(false);
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
        profiles = ProfileStore.NormalizeLegacyBatteryProfile(
            profiles,
            out _,
            batteryProtectionDefault: null);
        validator.Validate(profiles);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfPreservationRequired();
            AggregateSnapshot snapshot = await CaptureAggregateAsync(cancellationToken);
            if (snapshot.IsEmpty)
            {
                throw new InvalidDataException(
                    "The battery configuration aggregate is absent; profile-only save was rejected because preferences cannot be reconstructed safely.");
            }
            CommittedSnapshot committed = ReadCommittedSnapshotForSave(snapshot);
            await SaveConfigurationTransactionAsync(
                profiles,
                committed.Configuration.Preferences,
                cancellationToken,
                committed.ConfigurationMigrated ? null : committed.PreferenceJson,
                snapshot);
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
        profiles = ProfileStore.NormalizeLegacyBatteryProfile(
            profiles,
            out _,
            batteryProtectionDefault: null);
        validator.Validate(profiles);
        validator.ValidatePreferences(preferences);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfPreservationRequired();
            AggregateSnapshot snapshot = await CaptureAggregateAsync(cancellationToken);
            if (!snapshot.IsEmpty)
            {
                _ = ReadCommittedSnapshotForSave(snapshot);
            }
            await SaveConfigurationTransactionAsync(
                profiles,
                preferences,
                cancellationToken,
                priorSnapshot: snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BatteryConfigurationInspection> LoadCoreAsync(
        bool materializeAdmittedState,
        CancellationToken cancellationToken)
    {
        AggregateSnapshot snapshot = await CaptureAggregateAsync(cancellationToken);
        if (ContainsFutureSchema(snapshot))
        {
            return Quarantined(
                ConfigurationQuarantineReason.FutureSchema,
                recovered: true,
                snapshot.IsEmpty);
        }

        if (snapshot.IsEmpty)
        {
            BatteryConfiguration configuration = SafeConfiguration(recovered: false);
            if (materializeAdmittedState)
            {
                await SaveConfigurationTransactionAsync(
                    configuration.Profiles,
                    configuration.Preferences,
                    cancellationToken,
                    priorSnapshot: snapshot);
                await InvokePostCommitBoundaryAsync(cancellationToken);
                try
                {
                    configuration = ReadCommittedSnapshot(
                        await CaptureAggregateAsync(cancellationToken)).Configuration;
                }
                catch (ConfigurationAggregateException exception)
                {
                    return Quarantined(exception.Reason, recovered: true, wasEmpty: false);
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    return Quarantined(
                        ConfigurationQuarantineReason.InvalidDocument,
                        recovered: true,
                        wasEmpty: false);
                }
            }
            return new BatteryConfigurationInspection(configuration, WasEmpty: true);
        }

        if (snapshot.IsLegacyCandidate)
        {
            try
            {
                ProfileSet migrated = ReadLegacy(snapshot.Profile.Contents);
                var configuration = new BatteryConfiguration(
                    migrated,
                    new BatteryAutomationPreferences(),
                    Recovered: false);
                if (materializeAdmittedState)
                {
                    await SaveConfigurationTransactionAsync(
                        configuration.Profiles,
                        configuration.Preferences,
                        cancellationToken,
                        priorSnapshot: snapshot);
                    await InvokePostCommitBoundaryAsync(cancellationToken);
                    configuration = ReadCommittedSnapshot(
                        await CaptureAggregateAsync(cancellationToken)).Configuration;
                }
                return new BatteryConfigurationInspection(configuration, WasEmpty: false);
            }
            catch (ConfigurationAggregateException exception)
            {
                return Quarantined(exception.Reason, recovered: true, wasEmpty: false);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return Quarantined(
                    HasSchemaVersion(snapshot.Profile.Contents)
                        ? ConfigurationQuarantineReason.IncompleteAggregate
                        : ConfigurationQuarantineReason.InvalidDocument,
                    recovered: true,
                    wasEmpty: false);
            }
        }

        if (!snapshot.IsComplete)
        {
            return Quarantined(
                ConfigurationQuarantineReason.IncompleteAggregate,
                recovered: true,
                wasEmpty: false);
        }

        try
        {
            CommittedSnapshot committed = ReadCommittedSnapshot(snapshot);
            if (committed.ConfigurationMigrated && materializeAdmittedState)
            {
                await SaveConfigurationTransactionAsync(
                    committed.Configuration.Profiles,
                    committed.Configuration.Preferences,
                    cancellationToken,
                    priorSnapshot: snapshot);
                await InvokePostCommitBoundaryAsync(cancellationToken);
                AggregateSnapshot migrated = await CaptureAggregateAsync(cancellationToken);
                committed = ReadCommittedSnapshot(migrated);
            }
            return new BatteryConfigurationInspection(
                committed.Configuration,
                WasEmpty: false);
        }
        catch (ConfigurationAggregateException exception)
        {
            return Quarantined(exception.Reason, recovered: true, wasEmpty: false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Quarantined(
                ConfigurationQuarantineReason.InvalidDocument,
                recovered: true,
                wasEmpty: false);
        }
    }

    private CommittedSnapshot ReadCommittedSnapshot(AggregateSnapshot snapshot)
    {
        if (!snapshot.IsComplete)
        {
            throw new ConfigurationAggregateException(
                "The battery configuration aggregate is incomplete.",
                ConfigurationQuarantineReason.IncompleteAggregate);
        }

        byte[] profileJson = snapshot.Profile.Contents;
        byte[] preferenceJson = snapshot.Preferences.Contents;
        byte[] markerJson = snapshot.Marker.Contents;

        RejectDuplicateMembers(markerJson);
        BatteryConfigurationCommit? marker = JsonSerializer.Deserialize<BatteryConfigurationCommit>(
            markerJson,
            ProfileStore.SerializerOptions);
        if (marker is null || !ProfileStore.IsSupportedSchemaVersion(marker.SchemaVersion))
        {
            throw new InvalidDataException("The battery configuration marker is unsupported.");
        }

        if (!FixedHashEquals(marker.ProfileSha256, Hash(profileJson))
            || !FixedHashEquals(marker.PreferencesSha256, Hash(preferenceJson)))
        {
            throw new ConfigurationAggregateException(
                "The battery configuration hashes do not match.",
                ConfigurationQuarantineReason.HashMismatch);
        }

        BatteryAutomationPreferences preferences = ReadPreferences(
            preferenceJson,
            marker.SchemaVersion);
        bool legacyProtectionEnabled = preferences.ProtectionEnabled ?? true;
        ProfileSet profiles = profileStore.ReadDocument(
            profileJson,
            out bool profileMigrated,
            out int profileSchemaVersion,
            legacyProtectionEnabled);
        if (profileSchemaVersion != marker.SchemaVersion)
        {
            throw new ConfigurationAggregateException(
                "The profile and battery configuration marker schemas do not match.",
                ConfigurationQuarantineReason.MixedSchema);
        }

        bool preferencesMigrated = preferences.ProtectionEnabled.HasValue;
        preferences = preferences with { ProtectionEnabled = null };
        return new CommittedSnapshot(
            new BatteryConfiguration(profiles, preferences, Recovered: false),
            profileJson,
            preferenceJson,
            markerJson,
            profileMigrated || preferencesMigrated);
    }

    private Task InvokePostCommitBoundaryAsync(CancellationToken cancellationToken) =>
        postCommitBoundary?.Invoke(cancellationToken) ?? Task.CompletedTask;

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
        profiles = ProfileStore.NormalizeLegacyBatteryProfile(
            profiles,
            out _,
            batteryProtectionDefault: true);
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
        if (document.Preferences is null)
        {
            throw new InvalidDataException("The battery preferences are missing.");
        }

        if (expectedSchemaVersion < ProfileStore.BatteryProtectionProfileSchemaVersion
            && !document.Preferences.ProtectionEnabled.HasValue)
        {
            throw new InvalidDataException(
                "The legacy battery protection preference is missing.");
        }
        if (expectedSchemaVersion >= ProfileStore.BatteryProtectionProfileSchemaVersion
            && document.Preferences.ProtectionEnabled.HasValue)
        {
            throw new InvalidDataException(
                "The current battery preference document contains a legacy setting.");
        }

        validator.ValidatePreferences(document.Preferences);
        return document.Preferences;
    }

    private async Task SaveConfigurationTransactionAsync(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        CancellationToken cancellationToken,
        byte[]? committedPreferenceJson = null,
        AggregateSnapshot? priorSnapshot = null)
    {
        preferences = preferences with { ProtectionEnabled = null };
        byte[] profileJson = SerializeProfiles(profiles);
        byte[] preferenceJson = committedPreferenceJson ??
            JsonSerializer.SerializeToUtf8Bytes(
                new BatteryPreferencesDocument(ProfileStore.CurrentSchemaVersion, preferences),
                ProfileStore.SerializerOptions);
        byte[] markerJson = SerializeMarker(profileJson, preferenceJson);
        AggregateSnapshot prior = priorSnapshot
            ?? await CaptureAggregateAsync(cancellationToken);
        TargetSnapshot profilePrior = prior.Profile;
        TargetSnapshot preferencePrior = prior.Preferences;
        TargetSnapshot markerPrior = prior.Marker;
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
        CommittedSnapshot verified = ReadCommittedSnapshot(
            await CaptureAggregateAsync(CancellationToken.None));
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
            TargetSnapshot restored = await CaptureTargetAsync(
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
            _ = ReadCommittedSnapshot(
                await CaptureAggregateAsync(CancellationToken.None));
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

    private static async Task<TargetSnapshot> CaptureTargetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return new TargetSnapshot(
                path,
                Exists: true,
                await File.ReadAllBytesAsync(path, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return new TargetSnapshot(path, Exists: false, []);
        }
        catch (DirectoryNotFoundException)
        {
            return new TargetSnapshot(path, Exists: false, []);
        }
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
        new BatteryAutomationPreferences(),
        recovered);

    private static BatteryConfigurationInspection Quarantined(
        ConfigurationQuarantineReason reason,
        bool recovered,
        bool wasEmpty) => new(
            SafeConfiguration(recovered) with
            {
                Disposition = ConfigurationLoadDisposition.Quarantined,
                QuarantineReason = reason,
            },
            wasEmpty);

    private void ThrowIfPreservationRequired()
    {
        if (preservationRequired)
        {
            throw new ConfigurationQuarantinedException(
                "Configuration is preserved in quarantine and cannot be changed until a validated reload.",
                ConfigurationQuarantineReason.InvalidDocument);
        }
    }

    private CommittedSnapshot ReadCommittedSnapshotForSave(
        AggregateSnapshot snapshot)
    {
        if (ContainsFutureSchema(snapshot))
        {
            throw new ConfigurationQuarantinedException(
                "The existing configuration uses a newer schema and was preserved in quarantine.",
                ConfigurationQuarantineReason.FutureSchema);
        }

        try
        {
            return ReadCommittedSnapshot(snapshot);
        }
        catch (ConfigurationAggregateException exception)
        {
            throw new ConfigurationQuarantinedException(
                "The existing configuration is preserved in quarantine.",
                exception.Reason);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new ConfigurationQuarantinedException(
                "The existing configuration is invalid and was preserved in quarantine.",
                ConfigurationQuarantineReason.InvalidDocument);
        }
    }

    private async Task<AggregateSnapshot> CaptureAggregateAsync(
        CancellationToken cancellationToken) => new(
            await CaptureTargetAsync(profileStore.Path, cancellationToken),
            await CaptureTargetAsync(preferencesPath, cancellationToken),
            await CaptureTargetAsync(markerPath, cancellationToken));

    private static bool ContainsFutureSchema(AggregateSnapshot snapshot) =>
        snapshot.Targets.Any(target =>
            target.Exists && ContainsFutureSchema(target.Contents));

    private static bool ContainsFutureSchema(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("schemaVersion")
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out int schemaVersion)
                    && schemaVersion > ProfileStore.CurrentSchemaVersion)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Another readable sibling can still prove that the aggregate is future data.
        }

        return false;
    }

    private static bool HasSchemaVersion(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

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

    private sealed record AggregateSnapshot(
        TargetSnapshot Profile,
        TargetSnapshot Preferences,
        TargetSnapshot Marker)
    {
        internal IReadOnlyList<TargetSnapshot> Targets =>
            [Profile, Preferences, Marker];

        internal bool IsEmpty => Targets.All(target => !target.Exists);

        internal bool IsComplete => Targets.All(target => target.Exists);

        internal bool IsLegacyCandidate =>
            Profile.Exists && !Preferences.Exists && !Marker.Exists;
    }

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
        bool ConfigurationMigrated);

    private sealed class ConfigurationAggregateException(
        string message,
        ConfigurationQuarantineReason reason)
        : Exception(message)
    {
        internal ConfigurationQuarantineReason Reason { get; } = reason;
    }
}

internal sealed record BatteryConfigurationInspection(
    BatteryConfiguration Configuration,
    bool WasEmpty);
