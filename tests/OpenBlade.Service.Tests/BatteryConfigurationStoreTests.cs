using System.Diagnostics;
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
    public async Task WindowsReplay_ConfigurationAggregateFixtureMatchesManagedWriter()
    {
        Paths paths = CreatePaths();
        BatteryConfiguration initial = await CreateStore(paths).LoadAsync(CancellationToken.None);
        string[] prior = await ReadTargetsAsync();
        JsonElement recovery = default;
        BatteryConfigurationStore writer = CreateStore(paths, async (boundary, cancellationToken) =>
        {
            if (boundary != BatteryConfigurationCommitBoundary.AfterProfileReplacement)
            {
                return;
            }

            using JsonDocument record = JsonDocument.Parse(
                await File.ReadAllBytesAsync(paths.Marker + ".recovery-required", cancellationToken));
            JsonElement root = record.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            string id = root.GetProperty("transactionId").GetString()!;
            Assert.True(Guid.TryParseExact(id, "N", out _));
            JsonElement[] targets = root.GetProperty("targets").EnumerateArray().ToArray();
            Assert.Equal(3, targets.Length);
            for (int i = 0; i < targets.Length; i++)
            {
                string target = paths.All.ElementAt(i);
                Assert.Equal(target, targets[i].GetProperty("path").GetString());
                Assert.True(targets[i].GetProperty("priorExists").GetBoolean());
                string backup = target + $".transaction-{id}.backup";
                Assert.Equal(backup, targets[i].GetProperty("backupPath").GetString());
                Assert.Equal(Encoding.UTF8.GetBytes(prior[i]), await File.ReadAllBytesAsync(backup, cancellationToken));
                Assert.Equal(target + $".transaction-{id}.restore",
                    targets[i].GetProperty("restorePath").GetString());
            }
            // Normalize only the random ID and the test-owned absolute root.
            string text = root.GetRawText().Replace(id, "0123456789abcdef0123456789abcdef",
                StringComparison.Ordinal);
            string encodedDirectory = JsonEncodedText.Encode(directory + Path.DirectorySeparatorChar)
                .ToString();
            using JsonDocument normalized = JsonDocument.Parse(text.Replace(
                encodedDirectory, @"C:\\Fixture\\", StringComparison.Ordinal));
            recovery = normalized.RootElement.Clone();
        });

        await writer.SaveProfilesAsync(initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) },
            CancellationToken.None);
        string[] committed = await ReadTargetsAsync();
        Assert.NotEqual(prior[0], committed[0]);
        Assert.Equal(prior[1], committed[1]);
        Assert.NotEqual(prior[2], committed[2]);
        Assert.False(File.Exists(paths.Marker + ".recovery-required"));

        JsonElement actual = JsonSerializer.SerializeToElement(new
        {
            legacy = LegacyJson.ReplaceLineEndings("\n"),
            prior,
            committed,
            recovery,
        });
        string fixture = Path.Combine(AppContext.BaseDirectory, "WindowsReplay", "v1",
            "configuration-aggregate.json");
        using JsonDocument expected = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual));

        async Task<string[]> ReadTargetsAsync() =>
            await Task.WhenAll(paths.All.Select(async path =>
                Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path))));
    }


    [Fact]
    public async Task WindowsReplay_ConfigurationInspectionMatchesManagedReader()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        string fixtures = Path.Combine(AppContext.BaseDirectory, "WindowsReplay", "v1");
        using JsonDocument aggregate = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(fixtures, "configuration-aggregate.json")));
        JsonElement committed = aggregate.RootElement.GetProperty("committed");
        using JsonDocument vectors = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(fixtures, "configuration-inspection.json")));
        foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
        {
            string profile = Document("profile", committed[0].GetString()!);
            string preferences = Document("preferences", committed[1].GetString()!);
            await File.WriteAllTextAsync(paths.Profiles, profile);
            if (vector.TryGetProperty("profileOnly", out JsonElement only) && only.GetBoolean())
            {
                File.Delete(paths.Preferences);
                File.Delete(paths.Marker);
            }
            else
            {
                string schema = vector.TryGetProperty("markerSchema", out JsonElement value)
                    ? value.GetString()!
                    : "25";
                string marker = $$"""
                    {"schemaVersion":{{schema}},"profileSha256":"{{Hash(profile)}}","preferencesSha256":"{{Hash(preferences)}}"}
                    """;
                await File.WriteAllTextAsync(paths.Preferences, preferences);
                await File.WriteAllTextAsync(paths.Marker, marker);
            }

            string actual;
            try
            {
                BatteryConfigurationInspection inspection = await CreateStore(paths).InspectAsync(CancellationToken.None);
                actual = inspection.Configuration.IsAuthoritative
                    ? "CommittedEnvelope"
                    : inspection.Configuration.QuarantineReason.ToString();
            }
            catch (InvalidOperationException)
            {
                actual = nameof(InvalidOperationException);
            }

            Assert.True(vector.GetProperty("managed").GetString() == actual,
                $"{vector.GetProperty("id").GetString()} returned {actual}.");

            string Document(string name, string original)
            {
                if (vector.TryGetProperty(name, out JsonElement document))
                {
                    return document.GetString()!;
                }
                return vector.TryGetProperty(name + "Schema", out JsonElement schema)
                    ? original.Replace("\"schemaVersion\": 25",
                        $"\"schemaVersion\": {schema.GetString()}", StringComparison.Ordinal)
                    : original;
            }
        }

        static string Hash(string document) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(document)));
    }

    [RustConfigurationExchangeFact]
    public async Task WindowsReplay_RustConfigurationWriterExchangesManagedBytes()
    {
        string executable = Environment.GetEnvironmentVariable(RustConfigurationExchangeFactAttribute.PathVariable)!;
        string fileName = Path.GetFileName(executable);
        Assert.True(Path.IsPathFullyQualified(executable)
            && fileName.StartsWith("openblade_windows_host-", StringComparison.Ordinal)
            && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(executable));

        Paths paths = CreatePaths();
        await CreateStore(paths).LoadAsync(CancellationToken.None);
        string[] managed = await Task.WhenAll(paths.All.Select(async path =>
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path))));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--exact");
        start.ArgumentList.Add("configuration_writer::tests::managed_oracle_exchange");
        start.ArgumentList.Add("--nocapture");
        start.Environment["OPENBLADE_CONFIGURATION_EXCHANGE"] = "1";
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(managed).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        string transcript = await output;
        Assert.True(process.ExitCode == 0, $"{transcript}{Environment.NewLine}{await errors}");
        const string prefix = "CONFIGURATION_OUTPUT=";
        string line = Assert.Single(transcript.Split('\n'), text => text.StartsWith(prefix, StringComparison.Ordinal));
        string[] rust = JsonSerializer.Deserialize<string[]>(line[prefix.Length..])!;
        Assert.Equal(3, rust.Length);
        foreach ((string path, string document) in paths.All.Zip(rust))
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(document), timeout.Token);
        }

        BatteryConfigurationInspection read = await CreateStore(paths).InspectAsync(timeout.Token);
        Assert.True(read.Configuration.IsAuthoritative);
        Assert.Equal(BatteryPolicy.Create(75), read.Configuration.Profiles.BatteryPolicy);
    }

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
        Assert.Equal(
            ThermalControlPolicy.MaintainDesiredState,
            configuration.Preferences.ThermalControlPolicy);
        Assert.Equal(
            ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending,
            configuration.Preferences.ThermalPolicyUpgradeDisclosure);
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
    public async Task LoadAsync_AbsentAggregateCreatesMaintainWithoutUpgradeDisclosure()
    {
        Paths paths = CreatePaths();

        BatteryConfiguration configuration = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(
            ThermalControlPolicy.MaintainDesiredState,
            configuration.Preferences.ThermalControlPolicy);
        Assert.Equal(
            ThermalPolicyUpgradeDisclosureState.NotRequired,
            configuration.Preferences.ThermalPolicyUpgradeDisclosure);
        Assert.All(paths.All, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task LoadAsync_FrozenSchema15AggregateMigratesToMaintainWithDisclosurePending()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(
                ProfileStore.FrozenThermalPolicyPredecessorSchemaVersion,
                ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = Encoding.UTF8.GetBytes(
            $$"""
              {
                "schemaVersion": {{ProfileStore.FrozenThermalPolicyPredecessorSchemaVersion}},
                "preferences": {
                  "alwaysOverrideExternalLightingChanges": false,
                  "lastKnownLighting": null,
                  "keyboardLightingControlMode": 0,
                  "allowUntestedFirmware": false,
                  "lightingRefreshRate": 0
                }
              }
              """);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.FrozenThermalPolicyPredecessorSchemaVersion,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        Assert.Equal(
            ThermalControlPolicy.MaintainDesiredState,
            loaded.Preferences.ThermalControlPolicy);
        Assert.Equal(
            ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending,
            loaded.Preferences.ThermalPolicyUpgradeDisclosure);
        using JsonDocument persistedPreferences = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Preferences));
        JsonElement persisted = persistedPreferences.RootElement
            .GetProperty("preferences");
        Assert.Equal(
            ProfileStore.CurrentSchemaVersion,
            persistedPreferences.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            (int)ThermalControlPolicy.MaintainDesiredState,
            persisted.GetProperty("thermalControlPolicy").GetInt32());
        Assert.Equal(
            (int)ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending,
            persisted.GetProperty("thermalPolicyUpgradeDisclosure").GetInt32());
        using JsonDocument persistedMarker = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Marker));
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Preferences)),
            persistedMarker.RootElement.GetProperty("preferencesSha256").GetString());
    }

    [Theory]
    [InlineData("thermalControlPolicy")]
    [InlineData("thermalPolicyUpgradeDisclosure")]
    [InlineData("fanStopSettings")]
    public async Task LoadAsync_CurrentSchemaMissingRequiredThermalMemberPreservesExactAggregate(
        string missingMember)
    {
        Paths paths = CreatePaths();
        _ = await CreateStore(paths).LoadAsync(
            CancellationToken.None);
        string preferenceJson = await File.ReadAllTextAsync(paths.Preferences);
        using JsonDocument parsed = JsonDocument.Parse(preferenceJson);
        Dictionary<string, object?> members = parsed.RootElement
            .GetProperty("preferences")
            .EnumerateObject()
            .Where(property => !property.NameEquals(missingMember))
            .ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
        byte[] changedPreferences = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = ProfileStore.CurrentSchemaVersion,
                preferences = members,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        byte[] profiles = await File.ReadAllBytesAsync(paths.Profiles);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.CurrentSchemaVersion,
                Hash(profiles),
                Hash(changedPreferences)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllBytesAsync(paths.Preferences, changedPreferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);
        FileSnapshot preserved = await ReadSnapshotAsync(paths);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Quarantined, loaded.Disposition);
        Assert.Equal(ThermalControlPolicy.Strict, loaded.Preferences.ThermalControlPolicy);
        Assert.Equal(ConfigurationQuarantineReason.InvalidDocument, loaded.QuarantineReason);
        AssertSnapshotEqual(preserved, await ReadSnapshotAsync(paths));
    }

    [Theory]
    [InlineData("thermalControlPolicy", 99)]
    [InlineData("thermalPolicyUpgradeDisclosure", 99)]
    public async Task LoadAsync_CurrentSchemaUndefinedThermalEnumPreservesExactAggregate(
        string member,
        int undefinedValue)
    {
        Paths paths = CreatePaths();
        _ = await CreateStore(paths).LoadAsync(CancellationToken.None);
        string preferences = await File.ReadAllTextAsync(paths.Preferences);
        string original = member == "thermalControlPolicy"
            ? $"\"{member}\": {(int)ThermalControlPolicy.MaintainDesiredState}"
            : $"\"{member}\": {(int)ThermalPolicyUpgradeDisclosureState.NotRequired}";
        byte[] changedPreferences = Encoding.UTF8.GetBytes(preferences.Replace(
            original,
            $"\"{member}\": {undefinedValue}",
            StringComparison.Ordinal));
        byte[] profiles = await File.ReadAllBytesAsync(paths.Profiles);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.CurrentSchemaVersion,
                Hash(profiles),
                Hash(changedPreferences)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllBytesAsync(paths.Preferences, changedPreferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);
        FileSnapshot preserved = await ReadSnapshotAsync(paths);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Quarantined, loaded.Disposition);
        Assert.Equal(ThermalControlPolicy.Strict, loaded.Preferences.ThermalControlPolicy);
        AssertSnapshotEqual(preserved, await ReadSnapshotAsync(paths));
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
            LightingRefreshRate: LightingRefreshRate.SixtyHz,
            AllowExperimentalFeatures: true);

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
        Assert.True(
            persisted.RootElement.GetProperty("preferences")
                .GetProperty("allowExperimentalFeatures")
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
    public async Task LoadAsync_Schema16EnablesAutomaticSupportAndPreservesPolicy()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        int priorSchema = ProfileStore.ExperimentalFeaturesConsentPredecessorSchemaVersion;
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(priorSchema, ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = Encoding.UTF8.GetBytes(
            $$"""
              {
                "schemaVersion": {{priorSchema}},
                "preferences": {
                  "thermalControlPolicy": {{(int)ThermalControlPolicy.Strict}},
                  "thermalPolicyUpgradeDisclosure": {{(int)ThermalPolicyUpgradeDisclosureState.Acknowledged}}
                }
              }
              """);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                priorSchema,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        Assert.True(loaded.Preferences.AllowExperimentalFeatures);
        Assert.Equal(ThermalControlPolicy.Strict, loaded.Preferences.ThermalControlPolicy);
        Assert.Equal(
            ThermalPolicyUpgradeDisclosureState.Acknowledged,
            loaded.Preferences.ThermalPolicyUpgradeDisclosure);
        using JsonDocument persisted = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Preferences));
        Assert.Equal(
            ProfileStore.CurrentSchemaVersion,
            persisted.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(
            persisted.RootElement.GetProperty("preferences")
                .GetProperty("allowExperimentalFeatures")
                .GetBoolean());
    }

    [Theory]
    [InlineData(17, false)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    [InlineData(18, true)]
    [InlineData(19, false)]
    [InlineData(19, true)]
    public async Task LoadAsync_ExperimentalPreferenceUsesAutomaticSupport(int priorSchema, bool priorEnable)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(priorSchema, ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryPreferencesDocument(
                priorSchema,
                new BatteryAutomationPreferences(
                    ThermalControlPolicy: ThermalControlPolicy.Strict,
                    ThermalPolicyUpgradeDisclosure:
                        ThermalPolicyUpgradeDisclosureState.Acknowledged,
                    AllowExperimentalFeatures: priorEnable)),
            options);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                priorSchema,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        Assert.True(loaded.Preferences.AllowExperimentalFeatures);
        using JsonDocument canonical = JsonDocument.Parse(await File.ReadAllBytesAsync(paths.Preferences));
        Assert.True(canonical.RootElement.GetProperty("preferences").GetProperty("allowExperimentalFeatures").GetBoolean());
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
    public async Task LoadAsync_Schema17RejectsExpandedBatteryPolicyAndPreservesAggregate()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        int priorSchema = ProfileStore.ExperimentalFeaturesConsentSchemaVersion;
        ProfileSet expanded = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(99),
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(priorSchema, expanded),
            options);
        byte[] preferences = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryPreferencesDocument(
                priorSchema,
                new BatteryAutomationPreferences(
                    ThermalControlPolicy: ThermalControlPolicy.Strict,
                    ThermalPolicyUpgradeDisclosure:
                        ThermalPolicyUpgradeDisclosureState.Acknowledged,
                    AllowExperimentalFeatures: true)),
            options);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                priorSchema,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);
        FileSnapshot preserved = await ReadSnapshotAsync(paths);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Quarantined, loaded.Disposition);
        Assert.Equal(ConfigurationQuarantineReason.InvalidDocument, loaded.QuarantineReason);
        AssertSnapshotEqual(preserved, await ReadSnapshotAsync(paths));
    }

    [Fact]
    public async Task SaveConfigurationAsync_Schema18PersistsExpandedBatteryPolicy()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(99),
        };
        var preferences = new BatteryAutomationPreferences(
            AllowExperimentalFeatures: true);

        await store.SaveConfigurationAsync(
            profiles,
            preferences,
            CancellationToken.None);
        BatteryConfiguration loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(BatteryPolicy.Create(99), loaded.Profiles.BatteryPolicy);
        Assert.True(loaded.Preferences.AllowExperimentalFeatures);
        using JsonDocument persisted = JsonDocument.Parse(
            await File.ReadAllBytesAsync(paths.Profiles));
        Assert.Equal(
            ProfileStore.CurrentSchemaVersion,
            persisted.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            99,
            persisted.RootElement.GetProperty("profiles")
                .GetProperty("batteryPolicy")
                .GetProperty("chargeLimitPercent")
                .GetInt32());
    }

    [Fact]
    public async Task LoadAsync_CurrentSchemaMissingLegacyExperimentalPreferenceUsesAutomaticSupport()
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(
                ProfileStore.CurrentSchemaVersion,
                ProfileDefaults.CreateSafeDefaults()),
            options);
        byte[] preferences = Encoding.UTF8.GetBytes(
            $$"""
              {
                "schemaVersion": {{ProfileStore.CurrentSchemaVersion}},
                "preferences": {
                  "thermalControlPolicy": {{(int)ThermalControlPolicy.Strict}},
                  "thermalPolicyUpgradeDisclosure": {{(int)ThermalPolicyUpgradeDisclosureState.Acknowledged}},
                  "fanStopSettings": { "cpuImmediateCutoffCelsius": 65, "gpuImmediateCutoffCelsius": 65 }
                }
              }
              """);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.CurrentSchemaVersion,
                Hash(profiles),
                Hash(preferences)),
            options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferences);
        await File.WriteAllBytesAsync(paths.Marker, marker);

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(
            CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        Assert.True(loaded.Preferences.AllowExperimentalFeatures);
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task SaveAsync_FailedRollbackRetainsExactPriorAggregateForRecovery(
        bool profilesOnly,
        bool afterMarkerReplacement)
    {
        Paths paths = CreatePaths();
        bool armed = false;
        FileStream? destinationLock = null;
        var store = CreateStore(
            paths,
            (boundary, _) =>
            {
                BatteryConfigurationCommitBoundary failureBoundary = afterMarkerReplacement
                    ? BatteryConfigurationCommitBoundary.AfterMarkerReplacement
                    : BatteryConfigurationCommitBoundary.AfterProfileReplacement;
                if (armed && boundary == failureBoundary)
                {
                    // This permits backup reads and cleanup but prevents the prior
                    // profile from replacing the already submitted destination.
                    destinationLock = new FileStream(
                        paths.Profiles, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("Injected commit failure with rollback destination locked.");
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

        IOException failure;
        try
        {
            failure = await Assert.ThrowsAsync<IOException>(() => profilesOnly
                ? store.SaveProfilesAsync(intendedProfiles, CancellationToken.None)
                : store.SaveConfigurationAsync(
                    intendedProfiles, priorConfiguration.Preferences, CancellationToken.None));
        }
        finally
        {
            destinationLock?.Dispose();
        }

        AggregateException causes = Assert.IsType<AggregateException>(failure.InnerException);
        Assert.Equal(2, causes.InnerExceptions.Count);
        string[] backups = Directory.GetFiles(directory, "*.transaction-*.backup");
        Assert.Equal(paths.All.Length, backups.Length);
        string transactionSuffix = backups.Single(path => path.StartsWith(
            paths.Profiles + ".transaction-", StringComparison.Ordinal))[
                paths.Profiles.Length..^".backup".Length];
        Assert.Contains(transactionSuffix[".transaction-".Length..], failure.Message, StringComparison.Ordinal);
        foreach ((string path, byte[] expected) in new[]
        {
            (paths.Profiles, prior.Profiles),
            (paths.Preferences, prior.Preferences),
            (paths.Marker, prior.Marker),
        })
        {
            Assert.Contains(path, failure.Message, StringComparison.Ordinal);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path + transactionSuffix + ".backup"));
        }

        // Retaining recovery bytes does not make the mixed primary aggregate
        // authoritative, or allow a later load to restore it automatically.
        FileSnapshot failed = await ReadSnapshotAsync(paths);
        Assert.False(SnapshotEquals(prior, failed));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.SaveProfilesAsync(intendedProfiles, CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.LoadAsync(CancellationToken.None));
        BatteryConfigurationStore newStore = CreateStore(paths);
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            newStore.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            newStore.SaveConfigurationAsync(
                intendedProfiles, priorConfiguration.Preferences, CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            CreateStore(paths).SaveProfilesAsync(intendedProfiles, CancellationToken.None));
        AssertSnapshotEqual(failed, await ReadSnapshotAsync(paths));
        Assert.Equal(backups.Order(), Directory.GetFiles(directory, "*.transaction-*.backup").Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_FailedInitialRollbackDistinguishesAbsentPriorFiles(
        bool legacyProfilePresent)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        if (legacyProfilePresent)
        {
            await File.WriteAllTextAsync(paths.Profiles, LegacyJson);
        }
        FileStream? destinationLock = null;
        BatteryConfigurationStore store = CreateStore(paths, (boundary, _) =>
        {
            if (boundary == BatteryConfigurationCommitBoundary.AfterMarkerReplacement)
            {
                destinationLock = new FileStream(
                    paths.Profiles, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new IOException("Injected initial commit failure with rollback destination locked.");
            }
            return Task.CompletedTask;
        });

        IOException failure;
        try
        {
            failure = await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            destinationLock?.Dispose();
        }

        string[] backups = Directory.GetFiles(directory, "*.transaction-*.backup");
        Assert.Equal(legacyProfilePresent ? 1 : 0, backups.Length);
        Assert.Contains("Prior file absent (no backup)", failure.Message, StringComparison.Ordinal);
        if (legacyProfilePresent)
        {
            string backup = Assert.Single(backups);
            Assert.Contains(backup, failure.Message, StringComparison.Ordinal);
            Assert.Equal(LegacyJson, await File.ReadAllTextAsync(backup));
        }
        FileSnapshot failed = await ReadSnapshotAsync(paths);
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            CreateStore(paths).LoadAsync(CancellationToken.None));
        AssertSnapshotEqual(failed, await ReadSnapshotAsync(paths));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a valid recovery record")]
    public async Task RecoveryRequiredRecord_BlocksCoherentAggregateWithoutTrustingItsContents(string contents)
    {
        Paths paths = CreatePaths();
        BatteryConfiguration initial = await CreateStore(paths).LoadAsync(CancellationToken.None);
        FileSnapshot prior = await ReadSnapshotAsync(paths);
        string recoveryPath = paths.Marker + ".recovery-required";
        await File.WriteAllTextAsync(recoveryPath, contents);

        BatteryConfigurationStore store = CreateStore(paths);
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.InspectAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            CreateStore(paths).SaveConfigurationAsync(
                initial.Profiles, initial.Preferences, CancellationToken.None));
        AssertSnapshotEqual(prior, await ReadSnapshotAsync(paths));
        Assert.Equal(contents, await File.ReadAllTextAsync(recoveryPath));
    }

    [Fact]
    public async Task SaveAsync_RecoveryRecordCannotBeCreatedStopsBeforePrimaryReplacement()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        BatteryConfiguration initial = await store.LoadAsync(CancellationToken.None);
        FileSnapshot prior = await ReadSnapshotAsync(paths);
        // Make only the record's creation fail after ordinary save admission.
        // The primary targets remain writable, so this tests the pre-write gate.
        string recoveryPath = paths.Marker + ".recovery-required";
        bool blocked = false;
        BatteryConfigurationStore guarded = CreateStore(paths, (boundary, _) =>
        {
            if (boundary == BatteryConfigurationCommitBoundary.BeforeProfileReplacement)
            {
                Directory.CreateDirectory(recoveryPath);
                blocked = true;
            }
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            guarded.SaveProfilesAsync(initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) },
                CancellationToken.None));

        Assert.True(blocked);
        AssertSnapshotEqual(prior, await ReadSnapshotAsync(paths));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            CreateStore(paths).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_RecoveryRecordCleanupFailureRetainsEvidenceAndReportsCommittedState()
    {
        Paths paths = CreatePaths();
        BatteryConfiguration initial = await CreateStore(paths).LoadAsync(CancellationToken.None);
        FileSnapshot prior = await ReadSnapshotAsync(paths);
        FileStream? recordLock = null;
        string recoveryPath = paths.Marker + ".recovery-required";
        BatteryConfigurationStore store = CreateStore(paths, (boundary, _) =>
        {
            if (boundary == BatteryConfigurationCommitBoundary.AfterVerification)
            {
                recordLock = new FileStream(
                    recoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            return Task.CompletedTask;
        });

        IOException failure;
        try
        {
            failure = await Assert.ThrowsAsync<IOException>(() =>
                store.SaveProfilesAsync(initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) },
                    CancellationToken.None));
        }
        finally
        {
            recordLock?.Dispose();
        }

        Assert.Contains("committed and verified", failure.Message, StringComparison.Ordinal);
        Assert.Contains(recoveryPath, failure.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(recoveryPath));
        Assert.Equal(3, Directory.GetFiles(directory, "*.transaction-*.backup").Length);
        FileSnapshot committed = await ReadSnapshotAsync(paths);
        Assert.False(SnapshotEquals(prior, committed));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            CreateStore(paths).SaveConfigurationAsync(
                initial.Profiles, initial.Preferences, CancellationToken.None));
        AssertSnapshotEqual(committed, await ReadSnapshotAsync(paths));
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAsync_RoundTripsConfiguredThxProfilesAsVerifiedAggregate(
        bool profilesOnly)
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        BatteryConfiguration initial = await store.LoadAsync(CancellationToken.None);
        ThxAudioProfileSettings audio = CreateConfiguredThxAudioProfile();
        Assert.True(ThxAudioProfileSettings.IsWellFormed(audio));
        ProfileSet profiles = initial.Profiles with
        {
            PluggedIn = initial.Profiles.PluggedIn with { ThxAudio = audio },
            Battery = initial.Profiles.Battery with
            {
                ThxAudio = audio with { ApplicationRoutingEnabled = false },
            },
            UsbC = initial.Profiles.UsbC with { ThxAudio = audio },
        };
        BatteryAutomationPreferences preferences = profilesOnly
            ? initial.Preferences
            : initial.Preferences with { AlwaysOverrideExternalLightingChanges = true };
        byte[] priorPreferences = await File.ReadAllBytesAsync(paths.Preferences);

        if (profilesOnly)
        {
            await store.SaveProfilesAsync(profiles, CancellationToken.None);
            Assert.Equal(priorPreferences, await File.ReadAllBytesAsync(paths.Preferences));
        }
        else
        {
            await store.SaveConfigurationAsync(profiles, preferences, CancellationToken.None);
        }

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(CancellationToken.None);
        Assert.True(loaded.IsAuthoritative);
        Assert.Equal(profiles, loaded.Profiles);
        Assert.Equal(preferences, loaded.Preferences);
        Assert.NotSame(audio, loaded.Profiles.PluggedIn.ThxAudio);
        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllBytesAsync(paths.Marker));
        Assert.Equal(Hash(await File.ReadAllBytesAsync(paths.Profiles)),
            marker.RootElement.GetProperty("profileSha256").GetString());
        Assert.Equal(Hash(await File.ReadAllBytesAsync(paths.Preferences)),
            marker.RootElement.GetProperty("preferencesSha256").GetString());
        Assert.Empty(Directory.GetFiles(directory, "*.transaction-*"));
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
        LegacyJson.Replace("\"chargeLimitPercent\": 80", "\"chargeLimitPercent\": 52", StringComparison.Ordinal),
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

    private static ThxAudioProfileSettings CreateConfiguredThxAudioProfile()
    {
        static ThxAudioEndpointSettings Endpoint(int bandCount, bool speakers)
        {
            double[] Curve(double gain) => Enumerable.Repeat(gain, bandCount).ToArray();
            return new ThxAudioEndpointSettings(
                SpatialEnabled: true,
                EqualizerEnabled: true,
                ThxEqualizerPreset.Music,
                new ThxEqualizerCurves(Curve(1), Curve(-2), Curve(3), Curve(-4), Curve(5)),
                new ThxAudioEnhancementSettings(true, 40),
                speakers ? new ThxAudioEnhancementSettings(false, 60) : null);
        }

        return new ThxAudioProfileSettings(
            ApplicationRoutingEnabled: true,
            Endpoint(ThxAudioProfileSettings.SpeakerEqualizerBandCount, speakers: true),
            Endpoint(ThxAudioProfileSettings.HeadphoneEqualizerBandCount, speakers: false),
            [new("game.exe", ThxApplicationAudioOutput.Competitive),
                new("player.exe", ThxApplicationAudioOutput.Music)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_PreStopSchemaCannotArmAutomaticFanStop(bool injectFutureSettings)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        const int previousSchema = 23;
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(previousSchema, ProfileDefaults.CreateSafeDefaults()), options);
        var preferences = JsonSerializer.SerializeToNode(new BatteryAutomationPreferences(), options)!.AsObject();
        if (injectFutureSettings)
        {
            preferences["fanStopSettings"] = JsonSerializer.SerializeToNode(
                new FanStopSettings { AutomaticEnabled = true, EndMode = FanStopEndMode.Custom }, options);
        }
        else
        {
            preferences.Remove("fanStopSettings");
        }
        byte[] preferenceBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = previousSchema, preferences }, options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferenceBytes);
        await File.WriteAllBytesAsync(paths.Marker, JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(previousSchema, Hash(profiles), Hash(preferenceBytes)), options));

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        Assert.Equal(FanStopSettings.Default, loaded.Preferences.FanStopSettings);
        Assert.Equal(FanStopSettings.Default, (await CreateStore(paths).LoadAsync(CancellationToken.None)).Preferences.FanStopSettings);
    }

    [Fact]
    public async Task SaveConfigurationAsync_RoundTripsCustomFanStopPolicy()
    {
        Paths paths = CreatePaths();
        var settings = new FanStopSettings
        {
            EndMode = FanStopEndMode.Custom,
            CpuLimitCelsius = 61,
            GpuLimitCelsius = 64,
            CpuImmediateCutoffCelsius = 71,
            GpuImmediateCutoffCelsius = 79,
            SecondsAboveLimit = 11,
            AutomaticEnabled = true,
            SecondsBelowLimits = 40,
        };
        BatteryConfigurationStore store = CreateStore(paths);
        BatteryConfiguration initial = await store.LoadAsync(CancellationToken.None);
        await store.SaveConfigurationAsync(initial.Profiles, initial.Preferences with { FanStopSettings = settings }, CancellationToken.None);

        Assert.Equal(settings, (await CreateStore(paths).LoadAsync(CancellationToken.None)).Preferences.FanStopSettings);
    }

    [Theory]
    [InlineData(55, 65, 65, 75)]
    [InlineData(95, 100, 100, 100)]
    public async Task LoadAsync_ContinuousStopPolicyMigratesWithoutChangingSelectedIntent(
        int cpu, int gpu, int expectedCpuCutoff, int expectedGpuCutoff)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        const int previousSchema = 24;
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(previousSchema, ProfileDefaults.CreateSafeDefaults()), options);
        var selected = new FanStopSettings
        {
            EndMode = FanStopEndMode.Custom,
            CpuLimitCelsius = cpu,
            GpuLimitCelsius = gpu,
            SecondsAboveLimit = 17,
            AutomaticEnabled = true,
            SecondsBelowLimits = 42,
        };
        var preferences = JsonSerializer.SerializeToNode(
            new BatteryAutomationPreferences { FanStopSettings = selected }, options)!.AsObject();
        var stop = preferences["fanStopSettings"]!.AsObject();
        stop.Remove("cpuImmediateCutoffCelsius");
        stop.Remove("gpuImmediateCutoffCelsius");
        byte[] preferenceBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = previousSchema, preferences }, options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferenceBytes);
        await File.WriteAllBytesAsync(paths.Marker, JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(previousSchema, Hash(profiles), Hash(preferenceBytes)), options));

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(CancellationToken.None);
        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        var expected = selected with
        {
            CpuImmediateCutoffCelsius = expectedCpuCutoff,
            GpuImmediateCutoffCelsius = expectedGpuCutoff,
        };
        Assert.Equal(expected, loaded.Preferences.FanStopSettings);
        Assert.Equal(expected, (await CreateStore(paths).LoadAsync(CancellationToken.None)).Preferences.FanStopSettings);
    }

    [Theory]
    [InlineData("cpuImmediateCutoffCelsius")]
    [InlineData("gpuImmediateCutoffCelsius")]
    public async Task LoadAsync_CurrentStopPolicyCannotSilentlyDefaultMissingCutoff(string missing)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(ProfileStore.CurrentSchemaVersion, ProfileDefaults.CreateSafeDefaults()), options);
        var preferences = JsonSerializer.SerializeToNode(new BatteryAutomationPreferences(), options)!.AsObject();
        preferences["fanStopSettings"]!.AsObject().Remove(missing);
        byte[] preferenceBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = ProfileStore.CurrentSchemaVersion, preferences }, options);
        await File.WriteAllBytesAsync(paths.Profiles, profiles);
        await File.WriteAllBytesAsync(paths.Preferences, preferenceBytes);
        await File.WriteAllBytesAsync(paths.Marker, JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(ProfileStore.CurrentSchemaVersion, Hash(profiles), Hash(preferenceBytes)), options));

        BatteryConfiguration loaded = await CreateStore(paths).LoadAsync(CancellationToken.None);
        Assert.NotEqual(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
    }

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

internal sealed class RustConfigurationExchangeFactAttribute : FactAttribute
{
    internal const string PathVariable = "OPENBLADE_RUST_CONFIG_TEST_EXE";

    public RustConfigurationExchangeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable)))
        {
            Skip = $"Requires Windows and an explicit {PathVariable} path.";
        }
    }
}
