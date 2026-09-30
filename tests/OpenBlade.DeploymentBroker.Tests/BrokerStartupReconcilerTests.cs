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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Reconciliation_FirstInstallRollbackRequiresVerifiedAbsentState(
        bool previousRestored,
        bool expectedResolved)
    {
        ReconciliationFixture fixture = await CreateFixtureAsync(
            BrokerRecordedInstallerProcessStatus.NotRunning,
            new BrokerPostInstallVerification(false, false, previousRestored),
            installerExitCode: 1603,
            firstInstallBaseline: true);

        bool resolved = await fixture.Reconciler.TryReconcileAsync(
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(expectedResolved, resolved);
        Assert.Equal(!expectedResolved, File.Exists(fixture.Paths.InFlightPath));
        Assert.Equal(!expectedResolved, Directory.Exists(fixture.StagingDirectory));
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

    [Theory]
    [InlineData("request")]
    [InlineData("enrollment")]
    [InlineData("manifest")]
    [InlineData("package")]
    [InlineData("version")]
    [InlineData("sequence")]
    [InlineData("exit")]
    [InlineData("disposition")]
    public async Task Reconciliation_RejectsConflictingOriginalReport(string field)
    {
        var fixture = await CreateFixtureAsync(BrokerRecordedInstallerProcessStatus.NotRunning, new(true, true, false));
        var state = (await new BrokerInFlightStore(fixture.Paths).LoadAsync())!;
        var original = Report(state, fixture.Enrollment) with { Disposition = BrokerDeploymentDisposition.Indeterminate };
        original = field switch
        {
            "request" => original with { RequestId = Guid.NewGuid() },
            "enrollment" => original with { InstallationId = Guid.NewGuid() },
            "manifest" => original with { ManifestSha256 = new string('a', 64) },
            "package" => original with { PackageSha256 = new string('a', 64) },
            "version" => original with { TargetVersion = "65535.1.2.4" },
            "sequence" => original with { Sequence = original.Sequence + 1 },
            "exit" => original with { InstallerExitCode = 1603 },
            _ => original with { Disposition = BrokerDeploymentDisposition.RolledBack },
        };
        await new BrokerDurableRecordStore(fixture.Paths).CreateTerminalReportOnceAsync(original);
        string writtenPath = Path.Combine(fixture.Paths.ReportDirectory, original.RequestId.ToString("N") + ".json");
        string expectedPath = Path.Combine(fixture.Paths.ReportDirectory, state.RequestId.ToString("N") + ".json");
        if (writtenPath != expectedPath) { File.Move(writtenPath, expectedPath); }
        byte[] bytes = await File.ReadAllBytesAsync(expectedPath);
        Assert.False(await fixture.Reconciler.TryReconcileAsync(fixture.Enrollment, default));
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
        Assert.Empty(Directory.GetFiles(fixture.Paths.ReportDirectory, "*.reconciled.json"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expectedPath));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("state")]
    [InlineData("receipt")]
    [InlineData("original")]
    public async Task Reconciliation_ReceiptBindsSourceEntireStateAndOriginalReport(string changed)
    {
        var fixture = await CreateFixtureAsync(BrokerRecordedInstallerProcessStatus.NotRunning, new(true, true, false));
        var store = new BrokerDurableRecordStore(fixture.Paths);
        var state = (await new BrokerInFlightStore(fixture.Paths).LoadAsync())!;
        var report = Report(state, fixture.Enrollment);
        await store.CreateTerminalReportOnceAsync(report with { Disposition = BrokerDeploymentDisposition.Indeterminate });
        Assert.True(await fixture.Reconciler.TryReconcileAsync(fixture.Enrollment, default));
        string receipt = Path.Combine(fixture.Paths.ReportDirectory, state.RequestId.ToString("N") + ".reconciled.json");
        if (changed == "source") { state = state with { TargetSourceRevision = new string('a', 40) }; }
        if (changed == "state") { state = state with { PreviousPayloadSha256 = new string('a', 64) }; }
        if (changed == "receipt") { await File.AppendAllTextAsync(receipt, " "); }
        if (changed == "original")
        {
            string original = Path.Combine(fixture.Paths.ReportDirectory, state.RequestId.ToString("N") + ".json");
            string json = await File.ReadAllTextAsync(original);
            await File.WriteAllTextAsync(original, json.Replace("\"serviceHealthy\":true", "\"serviceHealthy\":false", StringComparison.Ordinal));
        }
        await File.WriteAllBytesAsync(fixture.Paths.InFlightPath, BrokerInFlightDeploymentCodec.SerializeCanonical(state));
        Assert.False(await fixture.Reconciler.TryReconcileAsync(fixture.Enrollment, default));
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
    }

    [Fact]
    public async Task Reconciliation_DifferentEnrolledSigningKeyCannotResolveOldRequest()
    {
        var fixture = await CreateFixtureAsync(BrokerRecordedInstallerProcessStatus.NotRunning, new(true, true, false));
        Assert.False(await fixture.Reconciler.TryReconcileAsync(
            fixture.Enrollment with { DevelopmentSigningKeyId = new string('0', 64) }, default));
        Assert.True(Directory.Exists(fixture.StagingDirectory));
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
    }

    private static BrokerTerminalReport Report(BrokerInFlightDeployment state, BrokerEnrollment enrollment) =>
        new(1, state.RequestId, enrollment.InstallationId, state.TargetSequence!.Value, enrollment.DeploymentPolicyId,
            BrokerDeploymentDisposition.Succeeded, state.ManifestSha256!, state.SigningKeyId!, state.InstallerSha256!,
            state.PreviousVersion!, state.TargetVersion!, state.InstallerExitCode, true,
            new DateTimeOffset(2026, 8, 27, 0, 0, 30, TimeSpan.Zero));

    [Fact]
    public async Task Reconciliation_IndeterminateCanResolveToVerifiedAbsentBaselineRollback()
    {
        var fixture = await CreateFixtureAsync(BrokerRecordedInstallerProcessStatus.NotRunning, new(false, false, true),
            installerExitCode: 1603, firstInstallBaseline: true);
        var state = (await new BrokerInFlightStore(fixture.Paths).LoadAsync())!;
        var records = new BrokerDurableRecordStore(fixture.Paths);
        await records.CreateTerminalReportOnceAsync(Report(state, fixture.Enrollment) with
        { Disposition = BrokerDeploymentDisposition.Indeterminate, ServiceHealthy = false });
        Assert.True(await fixture.Reconciler.TryReconcileAsync(fixture.Enrollment, default));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
        string receipt = Path.Combine(fixture.Paths.ReportDirectory, state.RequestId.ToString("N") + ".reconciled.json");
        Assert.Contains("RolledBack", await File.ReadAllTextAsync(receipt));
        await records.ValidateExistingJournalsAsync();
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
        int? installerExitCode = 0,
        bool firstInstallBaseline = false)
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerEnrollment enrollment = BrokerEnrollmentTests.CreateEnrollment();
        BrokerInFlightStore store = new(paths);
        Guid requestId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        await store.CreateOnceAsync(requestId, now);
        store.BindPreviousState(
            requestId,
            firstInstallBaseline
                ? BrokerInstalledState.NotInstalled
                : new BrokerInstalledState(
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
            enrollment.DevelopmentSigningKeyId,
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
            enrollment);
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
