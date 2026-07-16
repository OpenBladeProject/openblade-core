using System.Security.Cryptography;
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
        Assert.True(configuration.Preferences.ProtectionEnabled);
        Assert.All(paths.All, path => Assert.True(File.Exists(path)));
        Assert.All(paths.All, path => Assert.False(File.Exists(path + ".tmp")));

        using JsonDocument profiles = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Profiles));
        using JsonDocument preferences = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Preferences));
        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Marker));
        Assert.Equal(1, profiles.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, preferences.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, marker.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Profiles)),
            marker.RootElement.GetProperty("profileSha256").GetString());
        Assert.Equal(
            Hash(await File.ReadAllBytesAsync(paths.Preferences)),
            marker.RootElement.GetProperty("preferencesSha256").GetString());
        Assert.True(File.GetLastWriteTimeUtc(paths.Marker) >= File.GetLastWriteTimeUtc(paths.Profiles));
        Assert.True(File.GetLastWriteTimeUtc(paths.Marker) >= File.GetLastWriteTimeUtc(paths.Preferences));
    }

    [Theory]
    [MemberData(nameof(InvalidLegacyDocuments))]
    public async Task LoadAsync_InvalidLegacyProfileRecoversSafeEnabledDefaults(string json)
    {
        Paths paths = CreatePaths();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(paths.Profiles, json);

        BatteryConfiguration configuration = await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.True(configuration.Recovered);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), configuration.Profiles);
        Assert.True(configuration.Preferences.ProtectionEnabled);
        Assert.All(paths.All, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task LoadAsync_InterruptedSingleFileMigrationNeverCombinesDocuments()
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

        BatteryConfiguration configuration = await CreateStore(paths).LoadAsync(CancellationToken.None);

        Assert.True(configuration.Recovered);
        Assert.Equal(BatteryPolicy.Create(80), configuration.Profiles.BatteryPolicy);
        Assert.True(configuration.Preferences.ProtectionEnabled);
    }

    [Fact]
    public async Task LoadAsync_HashMismatchRecoversWholeAggregate()
    {
        Paths paths = CreatePaths();
        BatteryConfigurationStore store = CreateStore(paths);
        await store.LoadAsync(CancellationToken.None);
        string tampered = (await File.ReadAllTextAsync(paths.Profiles))
            .Replace("\"chargeLimitPercent\": 80", "\"chargeLimitPercent\": 50", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.Profiles, tampered);

        BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);

        Assert.True(configuration.Recovered);
        Assert.Equal(BatteryPolicy.Create(80), configuration.Profiles.BatteryPolicy);
        Assert.True(configuration.Preferences.ProtectionEnabled);
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
            new ProfileDocument(1, ProfileDefaults.CreateSafeDefaults()),
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
        BatteryConfigurationStore store = CreateStore(paths);
        ProfileSet updated = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        await store.SaveProfilesAsync(updated, CancellationToken.None);

        Assert.Equal(preferences, await File.ReadAllBytesAsync(paths.Preferences));
        BatteryConfiguration loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(updated, loaded.Profiles);
        Assert.False(loaded.Preferences.ProtectionEnabled);
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
        Assert.True(configuration.Preferences.ProtectionEnabled);
        BatteryConfiguration verified = await store.LoadAsync(CancellationToken.None);
        Assert.False(verified.Recovered);
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

    private BatteryConfigurationStore CreateStore(Paths paths) =>
        new(paths.Profiles, paths.Preferences, paths.Marker);

    private Paths CreatePaths() => new(
        Path.Combine(directory, "profiles.json"),
        Path.Combine(directory, "battery-preferences.json"),
        Path.Combine(directory, "battery-configuration.commit.json"));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

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
}
