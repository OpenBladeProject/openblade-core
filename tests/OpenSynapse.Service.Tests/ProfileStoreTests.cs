using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenSynapse.Tests",
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
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
