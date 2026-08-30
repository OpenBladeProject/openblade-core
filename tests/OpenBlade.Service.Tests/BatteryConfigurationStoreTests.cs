using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class BatteryConfigurationStoreTests : IDisposable
{
    private const string LegacyJson = """
        {
          "pluggedIn": { "performance": 1, "fans": 0, "lighting": { "effect": 0, "color": {}, "brightness": 0 } },
          "battery": { "performance": 0, "fans": 0, "lighting": { "effect": 0, "color": {}, "brightness": 0 } },
          "batteryPolicy": { "chargeLimitPercent": 80, "temporaryFullCharge": false }
        }
        """;

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.BatteryConfigurationStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_MigratesLegacyProfileAndCommitsMarkerLast()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(paths.Profiles, LegacyJson);
        BatteryConfigurationStore store = CreateStore(paths);

        BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);

        Assert.False(configuration.Recovered);
        Assert.Equal(BatteryPolicy.Create(80), configuration.Profiles.BatteryPolicy);
        Assert.Equal(PerformancePreset.Balanced, configuration.Profiles.PluggedIn.Performance);
        Assert.Equal(PerformancePreset.BatterySaver, configuration.Profiles.Battery.Performance);
        AssertBatteryProtection(configuration.Profiles, expected: true);
        Assert.Null(configuration.Preferences.ProtectionEnabled);
        Assert.All(paths.All, path => Assert.True(File.Exists(path)));
        Assert.All(paths.All, path => Assert.False(File.Exists(path + ".tmp")));

        using JsonDocument profiles = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Profiles));
        using JsonDocument preferences = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Preferences));
        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Marker));
        Assert.Equal(ProfileStore.CurrentSchemaVersion, profiles.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ProfileStore.CurrentSchemaVersion, preferences.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ProfileStore.CurrentSchemaVersion, marker.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Profiles)),
            marker.RootElement.GetProperty("profileSha256").GetString());
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Preferences)),
            marker.RootElement.GetProperty("preferencesSha256").GetString());
        Assert.True(File.GetLastWriteTimeUtc(paths.Marker) >= File.GetLastWriteTimeUtc(paths.Profiles));
        Assert.True(File.GetLastWriteTimeUtc(paths.Marker) >= File.GetLastWriteTimeUtc(paths.Preferences));
    }

    [Fact]
    public async Task SaveConfigurationAsync_RoundTripsLightingOwnershipPreferences()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        BatteryConfiguration initial = await store.LoadAsync(CancellationToken.None);
        LightingSettings lastKnown = initial.Profiles.PluggedIn.Lighting;
        var preferences = new BatteryAutomationPreferences(
            AlwaysOverrideExternalLightingChanges: true,
            LastKnownLighting: lastKnown,
            AllowUntestedFirmware: true,
            LightingRefreshRate: LightingRefreshRate.SixtyHz);

        await store.SaveConfigurationAsync(
            initial.Profiles,
            preferences,
            CancellationToken.None);
        BatteryConfiguration loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(preferences, loaded.Preferences);
        using JsonDocument persisted = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Preferences));
        Assert.Equal(
            ProfileStore.CurrentSchemaVersion,
            persisted.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(
            persisted.RootElement.GetProperty("preferences")
                .GetProperty("alwaysOverrideExternalLightingChanges")
                .GetBoolean());
        Assert.True(
            persisted.RootElement.GetProperty("preferences")
                .GetProperty("allowUntestedFirmware")
                .GetBoolean());
        Assert.Equal(
            (int)LightingRefreshRate.SixtyHz,
            persisted.RootElement.GetProperty("preferences")
                .GetProperty("lightingRefreshRate")
                .GetInt32());
        Assert.Equal(
            (int)LightingEffect.Off,
            persisted.RootElement.GetProperty("preferences")
                .GetProperty("lastKnownLighting")
                .GetProperty("effect")
                .GetInt32());
    }

    [Fact]
    public async Task LoadAsync_MigratesSchema13WithoutLightingRefreshRateToThirtyHz()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(
                ProfileStore.LatestSupportedSchemaVersion,
                ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = Encoding.UTF8.GetBytes(
            $$"""
              {
                "schemaVersion": {{ProfileStore.LatestSupportedSchemaVersion}},
                "preferences": {}
              }
              """);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.LatestSupportedSchemaVersion,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(
            LightingRefreshRate.ThirtyHz,
            loaded.Preferences.LightingRefreshRate);
        using JsonDocument persistedProfiles = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Profiles));
        using JsonDocument persistedPreferences = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Preferences));
        using JsonDocument persistedMarker = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Marker));
        Assert.Equal(
            ProfileStore.CurrentSchemaVersion,
            persistedProfiles.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ProfileStore.CurrentSchemaVersion,
            persistedPreferences.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            (int)LightingRefreshRate.ThirtyHz,
            persistedPreferences.RootElement.GetProperty("preferences")
                .GetProperty("lightingRefreshRate")
                .GetInt32());
        Assert.Equal(
            ProfileStore.CurrentSchemaVersion,
            persistedMarker.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Profiles)),
            persistedMarker.RootElement.GetProperty("profileSha256").GetString());
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Preferences)),
            persistedMarker.RootElement.GetProperty("preferencesSha256").GetString());
    }

    [Theory]
    [InlineData(0, PerformancePreset.BatterySaver)]
    [InlineData(1, PerformancePreset.BatteryBalanced)]
    public async Task LoadAsync_NormalizesLegacyBatteryPresetWithoutChangingPluggedIn(
        int legacyBatteryPerformance,
        PerformancePreset expectedBatteryPerformance)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        string legacy = LegacyJson.Replace(
            "\"battery\": { \"performance\": 0",
            $"\"battery\": {{ \"performance\": {legacyBatteryPerformance}",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.Profiles, legacy);

        BatteryConfiguration configuration =
            await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.False(configuration.Recovered);
        Assert.Equal(PerformancePreset.Balanced, configuration.Profiles.PluggedIn.Performance);
        Assert.Equal(expectedBatteryPerformance, configuration.Profiles.Battery.Performance);
    }

    [Fact]
    public async Task LoadAsync_MigratesCommittedLegacyBatteryPresetAndRehashesAggregate()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        ProfileSet legacyProfiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Performance = PerformancePreset.Silent,
                BatteryProtectionEnabled = null,
            },
            Battery = ProfileDefaults.CreateSafeDefaults().Battery with
            {
                Performance = PerformancePreset.Balanced,
                BatteryProtectionEnabled = null,
            },
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new LegacyProfileDocument(
                1,
                new LegacyProfileSet(
                    legacyProfiles.PluggedIn,
                    legacyProfiles.Battery,
                    legacyProfiles.BatteryPolicy)),
            options);
        byte[] preferences = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryPreferencesDocument(
                1,
                new BatteryAutomationPreferences(ProtectionEnabled: false)),
            options);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(1, Hash(profiles), Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);

        BatteryConfiguration configuration =
            await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.False(configuration.Recovered);
        Assert.Equal(PerformancePreset.Silent, configuration.Profiles.PluggedIn.Performance);
        Assert.Equal(
            PerformancePreset.BatteryBalanced,
            configuration.Profiles.Battery.Performance);
        Assert.Equal(
            ProfileDefaults.CreateSafeDefaults().UsbC with
            {
                BatteryProtectionEnabled = false,
            },
            configuration.Profiles.UsbC);
        AssertBatteryProtection(configuration.Profiles, expected: false);
        Assert.Null(configuration.Preferences.ProtectionEnabled);
        using JsonDocument persisted = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Profiles));
        Assert.Equal(
            (int)PerformancePreset.BatteryBalanced,
            persisted.RootElement
                .GetProperty("profiles")
                .GetProperty("battery")
                .GetProperty("performance")
                .GetInt32());
        using JsonDocument persistedMarker = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Marker));
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Profiles)),
            persistedMarker.RootElement.GetProperty("profileSha256").GetString());
    }

    [Theory]
    [MemberData(nameof(InvalidLegacyDocuments))]
    public async Task LoadAsync_InvalidLegacyProfilePreservesBytesInQuarantine(string json)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(paths.Profiles, json);
        byte[] original = await File.ReadAllBytesAsync(paths.Profiles);

        BatteryConfiguration configuration = await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.True(configuration.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, configuration.Disposition);
        Assert.NotEqual(ConfigurationQuarantineReason.None, configuration.QuarantineReason);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), configuration.Profiles);
        AssertBatteryProtection(configuration.Profiles, expected: true);
        Assert.Null(configuration.Preferences.ProtectionEnabled);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.Profiles));
        Assert.False(File.Exists(paths.Preferences));
        Assert.False(File.Exists(paths.Marker));
    }

    [Fact]
    public async Task LoadAsync_PartialModernAggregateIsQuarantinedWithoutMutation()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        string currentProfile = JsonSerializer.Serialize(
            new ProfileDocument(1, ProfileDefaults.CreateSafeDefaults() with
            {
                BatteryPolicy = BatteryPolicy.Create(50),
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(paths.Profiles, currentProfile);
        byte[] original = await File.ReadAllBytesAsync(paths.Profiles);

        BatteryConfiguration configuration = await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.True(configuration.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, configuration.Disposition);
        Assert.Equal(ConfigurationQuarantineReason.IncompleteAggregate, configuration.QuarantineReason);
        Assert.Equal(BatteryPolicy.Create(80), configuration.Profiles.BatteryPolicy);
        AssertBatteryProtection(configuration.Profiles, expected: true);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.Profiles));
        Assert.False(File.Exists(paths.Preferences));
        Assert.False(File.Exists(paths.Marker));
    }

    [Fact]
    public async Task LoadAsync_HashMismatchPreservesWholeAggregateInQuarantine()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        await store.LoadAsync(CancellationToken.None);
        string tampered = (await File.ReadAllTextAsync(paths.Profiles))
            .Replace("\"chargeLimitPercent\": 80", "\"chargeLimitPercent\": 50", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.Profiles, tampered);
        FileSnapshot preserved = await ReadSnapshotAsync(paths);

        BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);

        Assert.True(configuration.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, configuration.Disposition);
        Assert.Equal(ConfigurationQuarantineReason.HashMismatch, configuration.QuarantineReason);
        Assert.Equal(BatteryPolicy.Create(80), configuration.Profiles.BatteryPolicy);
        AssertBatteryProtection(configuration.Profiles, expected: true);
        AssertSnapshotEqual(preserved, await ReadSnapshotAsync(paths));
    }

    [Fact]
    public async Task LoadAsync_FutureSiblingTakesPrecedenceAndEverySavePreservesAggregate()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        BatteryConfiguration admitted = await store.LoadAsync(CancellationToken.None);
        await File.WriteAllTextAsync(paths.Profiles, "{invalid-json");
        await File.WriteAllTextAsync(
            paths.Preferences,
            $$"""
            {
              "schemaVersion": {{ProfileStore.CurrentSchemaVersion + 1}},
              "preferences": { "futureSetting": true }
            }
            """);
        FileSnapshot preserved = await ReadSnapshotAsync(paths);
        DateTime[] writeTimes = paths.All
            .Select(File.GetLastWriteTimeUtc)
            .ToArray();

        BatteryConfiguration quarantined = await store.LoadAsync(
            CancellationToken.None);
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.SaveConfigurationAsync(
                admitted.Profiles,
                admitted.Preferences,
                CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            CreateStore(paths).SaveConfigurationAsync(
                admitted.Profiles,
                admitted.Preferences,
                CancellationToken.None));

        Assert.Equal(ConfigurationLoadDisposition.Quarantined, quarantined.Disposition);
        Assert.Equal(ConfigurationQuarantineReason.FutureSchema, quarantined.QuarantineReason);
        AssertSnapshotEqual(preserved, await ReadSnapshotAsync(paths));
        Assert.Equal(writeTimes, paths.All.Select(File.GetLastWriteTimeUtc));
        Assert.Equal(
            paths.All.Order(StringComparer.OrdinalIgnoreCase),
            Directory.GetFiles(directory).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_Schema14AggregateMigratesAsOneAdmittedTransaction()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(14, ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryPreferencesDocument(14, new BatteryAutomationPreferences()),
            options);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(14, Hash(profiles), Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        foreach (string path in paths.All)
        {
            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllBytesAsync(path));
            Assert.Equal(
                ProfileStore.CurrentSchemaVersion,
                document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
    }

    [Fact]
    public async Task AggregateGate_SerializesIndependentStoreInstancesForSamePaths()
    {
        Paths paths = CreatePaths();
        BatteryConfiguration baseline = await CreateStore(paths).LoadAsync(
            CancellationToken.None);
        var enteredCommit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new BatteryConfigurationStore(
            paths.Profiles,
            paths.Preferences,
            paths.Marker,
            validator: null,
            postCommitBoundary: null,
            async (boundary, _) =>
            {
                if (boundary == BatteryConfigurationCommitBoundary.BeforeProfileReplacement)
                {
                    enteredCommit.TrySetResult();
                    await releaseCommit.Task;
                }
            });
        BatteryConfigurationStore reader = CreateStore(paths);
        ProfileSet intended = baseline.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(70),
        };

        Task write = writer.SaveConfigurationAsync(
            intended,
            baseline.Preferences,
            CancellationToken.None);
        await enteredCommit.Task;
        Task<BatteryConfiguration> read = reader.LoadAsync(CancellationToken.None);
        Assert.False(read.IsCompleted);

        releaseCommit.SetResult();
        await write;
        BatteryConfiguration loaded = await read;

        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        Assert.Equal(70, loaded.Profiles.BatteryPolicy.ChargeLimitPercent);
    }

    [Theory]
    [MemberData(nameof(InvalidPreferenceDocuments))]
    public async Task LoadAsync_InvalidPreferenceShapePreservesWholeAggregate(
        int schemaVersion,
        string preferenceJson)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(schemaVersion, ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = Encoding.UTF8.GetBytes(preferenceJson);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                schemaVersion,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);
        FileSnapshot preserved = await ReadSnapshotAsync(paths);

        BatteryConfiguration configuration =
            await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.True(configuration.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, configuration.Disposition);
        Assert.Equal(ConfigurationQuarantineReason.InvalidDocument, configuration.QuarantineReason);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), configuration.Profiles);
        Assert.Null(configuration.Preferences.ProtectionEnabled);
        AssertSnapshotEqual(preserved, await ReadSnapshotAsync(paths));
    }

    [Fact]
    public void RecoverableClassification_DoesNotTreatIoFailureAsInvalidConfiguration()
    {
        Assert.False(BatteryConfigurationStore.IsRecoverable(
            new IOException("transient read failure")));
    }

    [Fact]
    public async Task SaveProfilesAsync_PreservesServiceOwnedPreferenceBytes()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(ProfileStore.CurrentSchemaVersion, ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryPreferencesDocument(
                ProfileStore.CurrentSchemaVersion,
                new BatteryAutomationPreferences()),
            options);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.CurrentSchemaVersion,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);
        BatteryConfigurationStore store = CreateStore(paths);
        ProfileSet updated = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        await store.SaveProfilesAsync(updated, CancellationToken.None);

        Assert.Equal(preferences, await File.ReadAllBytesAsync(paths.Preferences));
        BatteryConfiguration loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(updated, loaded.Profiles);
        Assert.Null(loaded.Preferences.ProtectionEnabled);
    }

    [Fact]
    public async Task SaveProfilesAsync_EmptyAggregateRejectsWithoutReconstructingPreferences()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveProfilesAsync(
                ProfileDefaults.CreateSafeDefaults(),
                CancellationToken.None));

        Assert.All(paths.All, path => Assert.False(File.Exists(path)));
        Assert.Empty(Directory.Exists(directory)
            ? Directory.GetFiles(directory)
            : []);
    }

    [Theory]
    [MemberData(nameof(AllCommitBoundaries))]
    public async Task SaveProfilesAsync_FaultAtAnyBoundaryRestoresExactPriorAggregate(
        string boundaryName)
    {
        BatteryConfigurationCommitBoundary faultBoundary = Enum.Parse<
            BatteryConfigurationCommitBoundary>(boundaryName);
        Paths paths = CreatePaths();
        bool armed = false;
        var store = CreateStore(
            paths,
            (boundary, _) =>
            {
                if (armed && boundary == faultBoundary)
                {
                    throw new IOException($"Injected failure at {boundary}.");
                }

                return Task.CompletedTask;
            });
        BatteryConfiguration priorConfiguration = await store.LoadAsync(CancellationToken.None);
        FileSnapshot prior = await ReadSnapshotAsync(paths);
        ProfileSet intendedProfiles = priorConfiguration.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        armed = true;

        await Assert.ThrowsAsync<IOException>(
            () => store.SaveProfilesAsync(intendedProfiles, CancellationToken.None));

        AssertSnapshotEqual(prior, await ReadSnapshotAsync(paths));
        BatteryConfiguration loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(priorConfiguration.Profiles, loaded.Profiles);
        Assert.Equal(priorConfiguration.Preferences, loaded.Preferences);
        Assert.Empty(Directory.GetFiles(directory, "*.transaction-*"));
    }

    [Theory]
    [MemberData(nameof(AllCommitBoundaries))]
    public async Task SaveProfilesAsync_CancellationHonorsMarkerCommitPoint(
        string boundaryName)
    {
        BatteryConfigurationCommitBoundary cancellationBoundary = Enum.Parse<
            BatteryConfigurationCommitBoundary>(boundaryName);
        Paths paths = CreatePaths();
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var store = CreateStore(
            paths,
            (boundary, boundaryToken) =>
            {
                if (armed && boundary == cancellationBoundary)
                {
                    cancellation.Cancel();
                    boundaryToken.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            });
        BatteryConfiguration priorConfiguration = await store.LoadAsync(CancellationToken.None);
        FileSnapshot prior = await ReadSnapshotAsync(paths);
        ProfileSet intendedProfiles = priorConfiguration.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        armed = true;

        if (IsBeforeCommitPoint(cancellationBoundary))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.SaveProfilesAsync(intendedProfiles, cancellation.Token));

            AssertSnapshotEqual(prior, await ReadSnapshotAsync(paths));
            BatteryConfiguration restored = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(priorConfiguration.Profiles, restored.Profiles);
            Assert.Equal(priorConfiguration.Preferences, restored.Preferences);
        }
        else
        {
            await store.SaveProfilesAsync(intendedProfiles, cancellation.Token);

            BatteryConfiguration committed = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(intendedProfiles, committed.Profiles);
            Assert.Equal(priorConfiguration.Preferences, committed.Preferences);
            Assert.False(SnapshotEquals(prior, await ReadSnapshotAsync(paths)));
        }

        Assert.Empty(Directory.GetFiles(directory, "*.transaction-*"));
    }

    [Fact]
    public async Task SaveConfigurationAsync_CommitsProfilesAndPreferenceAsOneVerifiedAggregate()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        await store.LoadAsync(CancellationToken.None);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                BatteryProtectionEnabled = false,
            },
            Battery = ProfileDefaults.CreateSafeDefaults().Battery with
            {
                BatteryProtectionEnabled = false,
            },
            UsbC = ProfileDefaults.CreateSafeDefaults().UsbC with
            {
                BatteryProtectionEnabled = false,
            },
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        var preferences = new BatteryAutomationPreferences();

        await store.SaveConfigurationAsync(profiles, preferences, CancellationToken.None);

        BatteryConfiguration loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(profiles, loaded.Profiles);
        Assert.Equal(preferences, loaded.Preferences);
        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Marker));
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Profiles)),
            marker.RootElement.GetProperty("profileSha256").GetString());
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Preferences)),
            marker.RootElement.GetProperty("preferencesSha256").GetString());
    }

    [Theory]
    [MemberData(nameof(AllCommitBoundaries))]
    public async Task SaveConfigurationAsync_FaultAtAnyBoundaryRestoresExactPriorAggregate(
        string boundaryName)
    {
        BatteryConfigurationCommitBoundary faultBoundary = Enum.Parse<
            BatteryConfigurationCommitBoundary>(boundaryName);
        Paths paths = CreatePaths();
        bool armed = false;
        var store = CreateStore(
            paths,
            (boundary, _) =>
            {
                if (armed && boundary == faultBoundary)
                {
                    throw new IOException($"Injected failure at {boundary}.");
                }

                return Task.CompletedTask;
            });
        BatteryConfiguration priorConfiguration = await store.LoadAsync(CancellationToken.None);
        FileSnapshot prior = await ReadSnapshotAsync(paths);
        ProfileSet intendedProfiles = priorConfiguration.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        var intendedPreferences = new BatteryAutomationPreferences();
        armed = true;

        await Assert.ThrowsAsync<IOException>(
            () => store.SaveConfigurationAsync(
                intendedProfiles,
                intendedPreferences,
                CancellationToken.None));

        AssertSnapshotEqual(prior, await ReadSnapshotAsync(paths));
        BatteryConfiguration loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(priorConfiguration.Profiles, loaded.Profiles);
        Assert.Equal(priorConfiguration.Preferences, loaded.Preferences);
        Assert.Empty(Directory.GetFiles(directory, "*.transaction-*"));
    }

    [Theory]
    [MemberData(nameof(AllCommitBoundaries))]
    public async Task SaveConfigurationAsync_CancellationHonorsMarkerCommitPoint(
        string boundaryName)
    {
        BatteryConfigurationCommitBoundary cancellationBoundary = Enum.Parse<
            BatteryConfigurationCommitBoundary>(boundaryName);
        Paths paths = CreatePaths();
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var store = CreateStore(
            paths,
            (boundary, boundaryToken) =>
            {
                if (armed && boundary == cancellationBoundary)
                {
                    cancellation.Cancel();
                    boundaryToken.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            });
        BatteryConfiguration priorConfiguration = await store.LoadAsync(CancellationToken.None);
        FileSnapshot prior = await ReadSnapshotAsync(paths);
        ProfileSet intendedProfiles = priorConfiguration.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        var intendedPreferences = new BatteryAutomationPreferences();
        armed = true;

        if (IsBeforeCommitPoint(cancellationBoundary))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.SaveConfigurationAsync(
                    intendedProfiles,
                    intendedPreferences,
                    cancellation.Token));

            AssertSnapshotEqual(prior, await ReadSnapshotAsync(paths));
            BatteryConfiguration restored = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(priorConfiguration.Profiles, restored.Profiles);
            Assert.Equal(priorConfiguration.Preferences, restored.Preferences);
        }
        else
        {
            await store.SaveConfigurationAsync(
                intendedProfiles,
                intendedPreferences,
                cancellation.Token);

            BatteryConfiguration committed = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(intendedProfiles, committed.Profiles);
            Assert.Equal(intendedPreferences, committed.Preferences);
            Assert.False(SnapshotEquals(prior, await ReadSnapshotAsync(paths)));
        }

        Assert.Empty(Directory.GetFiles(directory, "*.transaction-*"));
    }

    [Fact]
    public async Task LoadAsync_PostCommitReplacementIsVerifiedBeforeAutomationReceivesConfiguration()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(paths.Profiles, LegacyJson);
        bool replaced = false;
        var store = new BatteryConfigurationStore(
            paths.Profiles,
            paths.Preferences,
            paths.Marker,
            validator: null,
            async cancellationToken =>
            {
                if (replaced)
                {
                    return;
                }

                replaced = true;
                byte[] replacement = JsonSerializer.SerializeToUtf8Bytes(
                    new BatteryPreferencesDocument(
                        1,
                        new BatteryAutomationPreferences(ProtectionEnabled: false)),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true,
                    });
                await File.WriteAllBytesAsync(
                    paths.Preferences,
                    replacement,
                    cancellationToken);
            });

        BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);

        Assert.True(replaced);
        Assert.True(configuration.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, configuration.Disposition);
        AssertBatteryProtection(configuration.Profiles, expected: true);
        Assert.Null(configuration.Preferences.ProtectionEnabled);
        BatteryConfiguration verified = await store.LoadAsync(CancellationToken.None);
        Assert.True(verified.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, verified.Disposition);
        Assert.Equal(configuration.Profiles, verified.Profiles);
        Assert.Equal(configuration.Preferences, verified.Preferences);
    }

    public static TheoryData<string> InvalidLegacyDocuments() => new()
    {
        LegacyJson.Replace("false", "true", StringComparison.Ordinal),
        LegacyJson.Replace("\"chargeLimitPercent\": 80", "\"chargeLimitPercent\": 80, \"chargeLimitPercent\": 75", StringComparison.Ordinal),
        LegacyJson.Replace("\"temporaryFullCharge\": false", "\"temporaryFullCharge\": false, \"unknown\": 1", StringComparison.Ordinal),
        LegacyJson.Replace("\"chargeLimitPercent\": 80", "\"chargeLimitPercent\": 81", StringComparison.Ordinal),
        LegacyJson.Replace("\"chargeLimitPercent\": 80", "\"chargeLimitPercent\": \"80\"", StringComparison.Ordinal),
        """
        { "schemaVersion": 2, "profiles": {} }
        """,
    };

    public static TheoryData<string> AllCommitBoundaries() => new(
        Enum.GetNames<BatteryConfigurationCommitBoundary>());

    public static TheoryData<int, string> InvalidPreferenceDocuments() => new()
    {
        {
            ProfileStore.OlderSchemaVersion,
            $$"""
            {
              "schemaVersion": {{ProfileStore.OlderSchemaVersion}},
              "preferences": {}
            }
            """
        },
        {
            ProfileStore.CurrentSchemaVersion,
            $$"""
            {
              "schemaVersion": {{ProfileStore.CurrentSchemaVersion}},
              "preferences": { "protectionEnabled": true }
            }
            """
        },
        {
            ProfileStore.CurrentSchemaVersion,
            $$"""
            {
              "schemaVersion": {{ProfileStore.CurrentSchemaVersion}},
              "preferences": null
            }
            """
        },
    };

    private BatteryConfigurationStore CreateStore(Paths paths) =>
        new(paths.Profiles, paths.Preferences, paths.Marker);

    private BatteryConfigurationStore CreateStore(
        Paths paths,
        Func<BatteryConfigurationCommitBoundary, CancellationToken, Task> boundary) =>
        new(
            paths.Profiles,
            paths.Preferences,
            paths.Marker,
            validator: null,
            postCommitBoundary: null,
            transactionBoundary: boundary);

    private Paths CreatePaths() => new(
        Path.Combine(directory, "profiles.json"),
        Path.Combine(directory, "battery-preferences.json"),
        Path.Combine(directory, "battery-configuration.commit.json"));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsBeforeCommitPoint(BatteryConfigurationCommitBoundary boundary) =>
        boundary is BatteryConfigurationCommitBoundary.BeforeProfileReplacement
            or BatteryConfigurationCommitBoundary.AfterProfileReplacement
            or BatteryConfigurationCommitBoundary.BeforePreferencesReplacement
            or BatteryConfigurationCommitBoundary.AfterPreferencesReplacement
            or BatteryConfigurationCommitBoundary.BeforeMarkerReplacement;

    private static async Task<FileSnapshot> ReadSnapshotAsync(Paths paths) => new(
        await File.ReadAllBytesAsync(paths.Profiles),
        await File.ReadAllBytesAsync(paths.Preferences),
        await File.ReadAllBytesAsync(paths.Marker));

    private static void AssertSnapshotEqual(FileSnapshot expected, FileSnapshot actual)
    {
        Assert.Equal(expected.Profiles, actual.Profiles);
        Assert.Equal(expected.Preferences, actual.Preferences);
        Assert.Equal(expected.Marker, actual.Marker);
    }

    private static bool SnapshotEquals(FileSnapshot left, FileSnapshot right) =>
        left.Profiles.AsSpan().SequenceEqual(right.Profiles)
        && left.Preferences.AsSpan().SequenceEqual(right.Preferences)
        && left.Marker.AsSpan().SequenceEqual(right.Marker);

    private static void AssertBatteryProtection(ProfileSet profiles, bool expected)
    {
        Assert.Equal(expected, profiles.PluggedIn.BatteryProtectionEnabled);
        Assert.Equal(expected, profiles.Battery.BatteryProtectionEnabled);
        Assert.Equal(expected, profiles.UsbC.BatteryProtectionEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record Paths(string Profiles, string Preferences, string Marker)
    {
        public string[] All => [Profiles, Preferences, Marker];
    }

    private sealed record FileSnapshot(
        byte[] Profiles,
        byte[] Preferences,
        byte[] Marker);
}
