using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class MachineProfileBootstrapperTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Service.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeAsync_PersistsSafeDefaultsWhenProfileFileIsMissing()
    {
        Paths paths = CreatePaths();
        MachineProfileBootstrapper bootstrapper = new(CreateStore(paths));

        BatteryConfiguration result = await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.False(result.Recovered);
        Assert.All(paths.All, path => Assert.True(File.Exists(path)));
        Assert.Equal(result, await CreateStore(paths).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsync_ReplacesCorruptProfileWithSafeDefaults()
    {
        Directory.CreateDirectory(directory);
        Paths paths = CreatePaths();
        await File.WriteAllTextAsync(paths.Profiles, "{bad json");
        MachineProfileBootstrapper bootstrapper = new(CreateStore(paths));

        BatteryConfiguration result = await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.True(result.Recovered);
        Assert.False((await CreateStore(paths).LoadAsync(CancellationToken.None)).Recovered);
    }

    private Paths CreatePaths() => new(
        Path.Combine(directory, "profiles.json"),
        Path.Combine(directory, "battery-preferences.json"),
        Path.Combine(directory, "battery-configuration.commit.json"));

    private static BatteryConfigurationStore CreateStore(Paths paths) =>
        new(paths.Profiles, paths.Preferences, paths.Marker);

    private sealed record Paths(string Profiles, string Preferences, string Marker)
    {
        public string[] All => [Profiles, Preferences, Marker];
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
