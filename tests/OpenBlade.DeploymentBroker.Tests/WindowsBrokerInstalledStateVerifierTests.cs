using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class WindowsBrokerInstalledStateVerifierTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.DeploymentBroker.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadBeforeInstall_RepresentsAbsentInstallWithoutWeakeningPathChecks()
    {
        WindowsBrokerInstalledStateVerifier verifier = new(
            new FixedHealthProbe(false),
            Path.Combine(root, "OpenBlade"));

        BrokerInstalledState state = await verifier.ReadBeforeInstallAsync(
            CancellationToken.None);

        Assert.Equal(BrokerInstalledState.NotInstalled, state);
        Assert.True(state.IsNotInstalled);
    }

    [Fact]
    public async Task ReadBeforeInstall_RejectsExistingPartialInstall()
    {
        string installDirectory = Path.Combine(root, "OpenBlade");
        Directory.CreateDirectory(installDirectory);
        WindowsBrokerInstalledStateVerifier verifier = new(
            new FixedHealthProbe(false),
            installDirectory);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            verifier.ReadBeforeInstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyCurrentState_DoesNotClaimAbsentBaselineWasRestored()
    {
        WindowsBrokerInstalledStateVerifier verifier = new(
            new FixedHealthProbe(true),
            Path.Combine(root, "OpenBlade"));

        BrokerPostInstallVerification verification = await verifier.VerifyCurrentStateAsync(
            BrokerInstalledState.NotInstalled,
            "65535.1.2.3",
            new string('a', 40),
            CancellationToken.None);

        Assert.False(verification.ExactTargetInstalled);
        Assert.True(verification.ServiceHealthy);
        Assert.False(verification.ExactPreviousStateRestored);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedHealthProbe(bool healthy) : IBrokerServiceHealthProbe
    {
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(healthy);
    }
}
