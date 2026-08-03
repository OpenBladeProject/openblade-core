using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class PerUserDynamicLightingRegistrationTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.PerUserDynamicLightingRegistrationTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactIdentity_DoesNotRedeployPackage()
    {
        int calls = 0;
        var registration = new PerUserDynamicLightingRegistration(
            new FixedIdentitySource(DynamicLightingPackageIdentityState.Exact),
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(true);
            },
            directory);

        bool changed = await registration.EnsureRegisteredAsync(
            CancellationToken.None);

        Assert.False(changed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingPackage_DoesNotAttemptRegistration()
    {
        Directory.CreateDirectory(directory);
        int calls = 0;
        var registration = new PerUserDynamicLightingRegistration(
            new FixedIdentitySource(DynamicLightingPackageIdentityState.Unavailable),
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(true);
            },
            directory);

        bool changed = await registration.EnsureRegisteredAsync(
            CancellationToken.None);

        Assert.False(changed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AvailablePackage_RegistersAgainstExactInstallDirectory()
    {
        Directory.CreateDirectory(directory);
        string packagePath = Path.Combine(directory, "OpenBlade.DynamicLighting.msix");
        await File.WriteAllBytesAsync(packagePath, [1]);
        Uri? observedPackage = null;
        Uri? observedExternalLocation = null;
        var registration = new PerUserDynamicLightingRegistration(
            new FixedIdentitySource(DynamicLightingPackageIdentityState.Unavailable),
            (package, externalLocation, _) =>
            {
                observedPackage = package;
                observedExternalLocation = externalLocation;
                return Task.FromResult(true);
            },
            directory);

        bool changed = await registration.EnsureRegisteredAsync(
            CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(new Uri(packagePath), observedPackage);
        Assert.Equal(
            new Uri(Path.TrimEndingDirectorySeparator(directory)
                + Path.DirectorySeparatorChar),
            observedExternalLocation);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedIdentitySource(DynamicLightingPackageIdentityState state)
        : IDynamicLightingPackageIdentitySource
    {
        public DynamicLightingPackageIdentityObservation Observe() => new(state, state);
    }
}
