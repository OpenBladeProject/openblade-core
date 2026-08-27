using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class BrokerStartupReconcilerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.DeploymentBroker.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(BrokerRecordedInstallerProcessStatus.RunningExactInstaller, true, true, false)]
    [InlineData(BrokerRecordedInstallerProcessStatus.Indeterminate, true, true, false)]
    [InlineData(BrokerRecordedInstallerProcessStatus.NotRunning, false, false, false)]
    [InlineData(BrokerRecordedInstallerProcessStatus.NotRunning, true, true, true)]
    public async Task Reconciliation_ClearsOnlyGoneProcessWithExactHealthyTarget(
        BrokerRecordedInstallerProcessStatus processStatus,
        bool exactTarget,
        bool healthy,
        bool expectedResolved)
    {
        ReconciliationFixture fixture = await CreateFixtureAsync(
            processStatus,
            new BrokerPostInstallVerification(exactTarget, healthy, false));

        bool resolved = await fixture.Reconciler.TryReconcileAsync(
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(expectedResolved, resolved);
        Assert.Equal(!expectedResolved, File.Exists(fixture.Paths.InFlightPath));
        Assert.Equal(!expectedResolved, Directory.Exists(fixture.StagingDirectory));
    }

    [Fact]
    public async Task Reconciliation_GoneProcessClearsOnlyExactCompleteRollback()
    {
        ReconciliationFixture fixture = await CreateFixtureAsync(
            BrokerRecordedInstallerProcessStatus.NotRunning,
            new BrokerPostInstallVerification(false, true, true));

        Assert.True(await fixture.Reconciler.TryReconcileAsync(
            fixture.Enrollment,
            CancellationToken.None));
        Assert.Null(await new BrokerInFlightStore(fixture.Paths).LoadAsync());
        Assert.False(Directory.Exists(fixture.StagingDirectory));
    }

    [Fact]
    public async Task Reconciliation_GoneProcessWithoutDurableExitResultRemainsAmbiguous()
    {
        ReconciliationFixture fixture = await CreateFixtureAsync(
            BrokerRecordedInstallerProcessStatus.NotRunning,
            new BrokerPostInstallVerification(true, true, false),
            installerExitCode: null);

        Assert.False(await fixture.Reconciler.TryReconcileAsync(
            fixture.Enrollment,
            CancellationToken.None));
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
        Assert.True(Directory.Exists(fixture.StagingDirectory));
    }

    [Fact]
    public async Task Reconciliation_NonzeroExitNeverInventsTargetSuccess()
    {
        ReconciliationFixture fixture = await CreateFixtureAsync(
            BrokerRecordedInstallerProcessStatus.NotRunning,
            new BrokerPostInstallVerification(true, true, false),
            installerExitCode: 1603);

        Assert.False(await fixture.Reconciler.TryReconcileAsync(
            fixture.Enrollment,
            CancellationToken.None));
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
    }

    [Fact]
    public async Task Reconciliation_ExistingHandlerReportWithDurableExitCodeIsIdempotent()
    {
        ReconciliationFixture fixture = await CreateFixtureAsync(
            BrokerRecordedInstallerProcessStatus.NotRunning,
            new BrokerPostInstallVerification(true, true, false));
        BrokerInFlightDeployment state = Assert.IsType<BrokerInFlightDeployment>(
            await new BrokerInFlightStore(fixture.Paths).LoadAsync());
        await new BrokerDurableRecordStore(fixture.Paths).CreateTerminalReportOnceAsync(
            new BrokerTerminalReport(
                1,
                state.RequestId,
                fixture.Enrollment.InstallationId,
                state.TargetSequence!.Value,
                "openblade-local-dev-v1",
                BrokerDeploymentDisposition.Succeeded,
                state.ManifestSha256!,
                state.SigningKeyId!,
                state.InstallerSha256!,
                state.PreviousVersion!,
                state.TargetVersion!,
                state.InstallerExitCode,
                true,
                new DateTimeOffset(2026, 8, 27, 0, 0, 30, TimeSpan.Zero)));

        Assert.True(await fixture.Reconciler.TryReconcileAsync(
            fixture.Enrollment,
            CancellationToken.None));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
    }

    [Fact]
    public void InstalledReceipt_CoversFixedLongLivedAndSharedIpcPayload()
    {
        Assert.Equal(
            [
                "OpenBlade.Service.exe",
                "OpenBlade.Service.dll",
                "OpenBlade.Protocol.dll",
                "OpenBlade.Contracts.dll",
                "OpenBlade.SessionAgent.exe",
                "OpenBlade.SessionAgent.dll",
                "OpenBlade.Session.dll",
                "OpenBlade.Tray.exe",
                "OpenBlade.Tray.dll",
                "OpenBlade.Tray.Core.dll",
            ],
            WindowsBrokerInstalledStateVerifier.ProtectedPayloadRelativePaths);
    }

    private async Task<ReconciliationFixture> CreateFixtureAsync(
        BrokerRecordedInstallerProcessStatus processStatus,
        BrokerPostInstallVerification verification,
        int? installerExitCode = 0)
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerInFlightStore store = new(paths);
        Guid requestId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        await store.CreateOnceAsync(requestId, now);
        store.BindPreviousState(
            requestId,
            new BrokerInstalledState(
                "1.0.0",
                "1.0.0+" + new string('a', 40),
                new string('b', 64)),
            now.AddSeconds(1));
        store.BindRelease(
            requestId,
            new string('c', 64),
            "65535.1.2.3",
            new string('d', 40),
            new string('e', 64),
            new string('f', 64),
            1,
            now.AddSeconds(2));
        store.BindInstallerProcess(requestId, 4242, now.AddSeconds(3));
        if (installerExitCode.HasValue)
        {
            store.BindInstallerResult(requestId, installerExitCode.Value, now.AddSeconds(4));
        }
        string staging = paths.CreateRequestStagingDirectory(requestId);
        BrokerStartupReconciler reconciler = new(
            store,
            new FakeProcessInspector(processStatus),
            new FakeInstalledStateVerifier(verification),
            new BrokerDurableRecordStore(paths),
            paths,
            new FixedTimeProvider(now.AddMinutes(1)),
            NullLogger<BrokerStartupReconciler>.Instance);
        return new ReconciliationFixture(
            reconciler,
            paths,
            staging,
            BrokerEnrollmentTests.CreateEnrollment());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeProcessInspector(BrokerRecordedInstallerProcessStatus status)
        : IBrokerRecordedInstallerProcessInspector
    {
        public Task<BrokerRecordedInstallerProcessStatus> InspectAsync(
            BrokerInFlightDeployment state,
            CancellationToken cancellationToken) => Task.FromResult(status);
    }

    private sealed class FakeInstalledStateVerifier(BrokerPostInstallVerification verification)
        : IBrokerInstalledStateVerifier
    {
        public Task<BrokerInstalledState> ReadBeforeInstallAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrokerPostInstallVerification> VerifyAfterInstallAsync(
            BrokerInstalledState before,
            OpenBladeVerifiedRelease expectedRelease,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrokerPostInstallVerification> VerifyCurrentStateAsync(
            BrokerInstalledState before,
            string expectedVersion,
            string expectedSourceRevision,
            CancellationToken cancellationToken) => Task.FromResult(verification);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record ReconciliationFixture(
        BrokerStartupReconciler Reconciler,
        BrokerPaths Paths,
        string StagingDirectory,
        BrokerEnrollment Enrollment);
}
