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
    public void CreateInstalledState_ReportsTheExactIdentityWhenThePayloadIsCoherent()
    {
        string productVersion = "0.20.7+" + new string('a', 40);

        BrokerInstalledState state = WindowsBrokerInstalledStateVerifier.CreateInstalledState(
            productVersion,
            productVersion,
            new string('c', 64));

        Assert.Equal("0.20.7", state.Version);
        Assert.Equal(productVersion, state.ProductVersion);
    }

    [Fact]
    public void CreateInstalledState_RecordsATrayOnlyDeploymentWithoutRefusingIt()
    {
        BrokerInstalledState state = WindowsBrokerInstalledStateVerifier.CreateInstalledState(
            "65535.1.2.3+" + new string('a', 40),
            "65535.1.2.9+" + new string('b', 40),
            new string('c', 64));

        // The lower member is the baseline, and the split stays visible instead of
        // being presented as either component's identity.
        Assert.Equal("65535.1.2.3", state.Version);
        Assert.StartsWith("mixed:", state.ProductVersion, StringComparison.Ordinal);
        Assert.DoesNotContain('+', state.ProductVersion);
        Assert.Equal(new string('c', 64), state.PayloadSha256);
    }

    [Fact]
    public void CreateInstalledState_IsStableSoRollbackStillProvesAnExactRestore()
    {
        BrokerInstalledState first = WindowsBrokerInstalledStateVerifier.CreateInstalledState(
            "65535.1.2.3+" + new string('a', 40),
            "65535.1.2.9+" + new string('b', 40),
            new string('c', 64));
        BrokerInstalledState second = WindowsBrokerInstalledStateVerifier.CreateInstalledState(
            "65535.1.2.3+" + new string('a', 40),
            "65535.1.2.9+" + new string('b', 40),
            new string('c', 64));

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateInstalledState_DistinguishesDifferentSplits()
    {
        BrokerInstalledState first = WindowsBrokerInstalledStateVerifier.CreateInstalledState(
            "65535.1.2.3+" + new string('a', 40),
            "65535.1.2.9+" + new string('b', 40),
            new string('c', 64));
        BrokerInstalledState second = WindowsBrokerInstalledStateVerifier.CreateInstalledState(
            "65535.1.2.3+" + new string('a', 40),
            "65535.1.2.9+" + new string('d', 40),
            new string('c', 64));

        Assert.NotEqual(first.ProductVersion, second.ProductVersion);
    }

    [Fact]
    public void CreateInstalledState_RejectsANoncanonicalMemberOfASplitPayload()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsBrokerInstalledStateVerifier.CreateInstalledState(
                "65535.1.2.3+" + new string('a', 40),
                "10.0.9-servicing.26270.113+" + new string('b', 40),
                new string('c', 64)));
    }

    [Fact]
    public void CreateInstalledState_KeepsASplitPayloadFromMatchingTheSignedBundle()
    {
        string expectedVersion = "65535.1.2.9";
        string expectedSourceRevision = new('b', 40);
        BrokerInstalledState split = WindowsBrokerInstalledStateVerifier.CreateInstalledState(
            "65535.1.2.3+" + new string('a', 40),
            expectedVersion + "+" + expectedSourceRevision,
            new string('c', 64));

        // The tray half matches the signed bundle exactly. The payload still must
        // not be reported as installed while the service half lags behind it.
        string[] parts = split.ProductVersion.Split('+', 2);
        Assert.Single(parts);
        Assert.NotEqual(expectedVersion + "+" + expectedSourceRevision, split.ProductVersion);
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
