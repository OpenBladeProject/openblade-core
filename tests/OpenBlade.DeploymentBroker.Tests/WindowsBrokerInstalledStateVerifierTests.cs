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
            new FixedHealthProbe(BrokerServiceObservation.Indeterminate),
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
            new FixedHealthProbe(BrokerServiceObservation.Indeterminate),
            installDirectory);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            verifier.ReadBeforeInstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyCurrentState_ClaimsAbsentBaselineOnlyWithPositiveServiceAbsence()
    {
        WindowsBrokerInstalledStateVerifier verifier = new(
            new FixedHealthProbe(BrokerServiceObservation.Absent),
            Path.Combine(root, "OpenBlade"));

        BrokerPostInstallVerification verification = await verifier.VerifyCurrentStateAsync(
            BrokerInstalledState.NotInstalled,
            "65535.1.2.3",
            new string('a', 40),
            CancellationToken.None);

        Assert.False(verification.ExactTargetInstalled);
        Assert.False(verification.ServiceHealthy);
        Assert.True(verification.ExactPreviousStateRestored);
    }

    [Theory]
    [InlineData(BrokerServiceObservation.Healthy, true)]
    [InlineData(BrokerServiceObservation.Indeterminate, false)]
    public async Task VerifyCurrentState_DoesNotTreatExistingOrUnobservableServiceAsAbsent(
        BrokerServiceObservation observation,
        bool expectedHealthy)
    {
        WindowsBrokerInstalledStateVerifier verifier = new(
            new FixedHealthProbe(observation),
            Path.Combine(root, "OpenBlade"),
            healthObservationTimeout: TimeSpan.Zero,
            healthObservationInterval: TimeSpan.Zero);

        BrokerPostInstallVerification verification = await verifier.VerifyCurrentStateAsync(
            BrokerInstalledState.NotInstalled,
            "65535.1.2.3",
            new string('a', 40),
            CancellationToken.None);

        Assert.False(verification.ExactTargetInstalled);
        Assert.Equal(expectedHealthy, verification.ServiceHealthy);
        Assert.False(verification.ExactPreviousStateRestored);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedHealthProbe(BrokerServiceObservation observation)
        : IBrokerServiceHealthProbe
    {
        public Task<BrokerServiceObservation> ObserveAsync(
            CancellationToken cancellationToken) => Task.FromResult(observation);
    }
}
