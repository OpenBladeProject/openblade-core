using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveThenLoad_RoundTripsProfiles()
    {
        string path = Path.Combine(_directory, "profiles.json");
        ProfileStore store = new(path);
        ProfileSet expected = ProfileDefaults.CreateSafeDefaults();

        await store.SaveAsync(expected, CancellationToken.None);
        ProfileLoadResult result = await store.LoadAsync(CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.Equal(expected, result.Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Load_CorruptJsonReturnsSafeDefaultsAndReportsRecovery()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "profiles.json");
        await File.WriteAllTextAsync(path, "{not-json", CancellationToken.None);
        ProfileStore store = new(path);

        ProfileLoadResult result = await store.LoadAsync(CancellationToken.None);

        Assert.True(result.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), result.Profiles);
        Assert.Equal(
            ProfileDefaults.CreateSafeDefaults(),
            (await store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Load_SemanticallyInvalidJsonAtomicallyRecoversSafeDefaults()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "profiles.json");
        string json = """
            {
              "pluggedIn": {
                "performance": 999,
                "fans": 0,
                "lighting": { "effect": 0, "color": { "red": 0, "green": 0, "blue": 0 }, "brightness": 0 }
              },
              "battery": {
                "performance": 0,
                "fans": 0,
                "lighting": { "effect": 0, "color": { "red": 0, "green": 0, "blue": 0 }, "brightness": 0 }
              },
              "batteryPolicy": { "chargeLimitPercent": 80, "temporaryFullCharge": false }
            }
            """;
        await File.WriteAllTextAsync(path, json, CancellationToken.None);
        ProfileStore store = new(path);

        ProfileLoadResult first = await store.LoadAsync(CancellationToken.None);
        ProfileLoadResult second = await store.LoadAsync(CancellationToken.None);

        Assert.True(first.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), first.Profiles);
        Assert.False(second.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), second.Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Save_InvalidProfilesThrowsBeforeReplacingExistingFile()
    {
        string path = Path.Combine(_directory, "profiles.json");
        ProfileStore store = new(path);
        ProfileSet valid = ProfileDefaults.CreateSafeDefaults();
        await store.SaveAsync(valid, CancellationToken.None);
        ProfileSet invalid = valid with
        {
            PluggedIn = valid.PluggedIn with { Fans = (FanMode)999 },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(invalid, CancellationToken.None));

        Assert.Equal(valid, (await store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Load_CaseVariantSchemaMemberRecoversInsteadOfBindingLoosely()
    {
        string path = Path.Combine(_directory, "profiles.json");
        ProfileStore store = new(path);
        await store.SaveAsync(ProfileDefaults.CreateSafeDefaults(), CancellationToken.None);
        string json = (await File.ReadAllTextAsync(path))
            .Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        ProfileLoadResult result = await store.LoadAsync(CancellationToken.None);

        Assert.True(result.RecoveredFromInvalidFile);
        Assert.Equal(ProfileDefaults.CreateSafeDefaults(), result.Profiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
