using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class MachineProfileBootstrapperTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenSynapse.Service.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeAsync_PersistsSafeDefaultsWhenProfileFileIsMissing()
    {
        string path = Path.Combine(directory, "profiles.json");
        MachineProfileBootstrapper bootstrapper = new(new ProfileStore(path));

        ProfileLoadResult result = await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.False(result.RecoveredFromInvalidFile);
        Assert.True(File.Exists(path));
        Assert.Equal(result.Profiles, (await new ProfileStore(path).LoadAsync(CancellationToken.None)).Profiles);
    }

    [Fact]
    public async Task InitializeAsync_ReplacesCorruptProfileWithSafeDefaults()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "profiles.json");
        await File.WriteAllTextAsync(path, "{bad json");
        MachineProfileBootstrapper bootstrapper = new(new ProfileStore(path));

        ProfileLoadResult result = await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.True(result.RecoveredFromInvalidFile);
        Assert.False((await new ProfileStore(path).LoadAsync(CancellationToken.None)).RecoveredFromInvalidFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
