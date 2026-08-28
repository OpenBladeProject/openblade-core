using System.Diagnostics;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class DeploymentPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.DeploymentBroker.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcceptedSequenceStore_DurablyConsumesBeforeRejectingReplay()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerAcceptedSequenceStore store = new(paths);

        await store.ConsumeAsync(7);

        Assert.Equal("7", await File.ReadAllTextAsync(paths.AcceptedSequencePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BrokerAcceptedSequenceStore(paths).ConsumeAsync(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BrokerAcceptedSequenceStore(paths).ConsumeAsync(6));
        await new BrokerAcceptedSequenceStore(paths).ConsumeAsync(8);
        Assert.Equal("8", await File.ReadAllTextAsync(paths.AcceptedSequencePath));
    }

    [Fact]
    public void TrustPolicyValidator_RejectsDisabledPolicy()
    {
        OpenBladeReleaseVerificationException exception = Assert.Throws<
            OpenBladeReleaseVerificationException>(() => BrokerTrustPolicyValidator.Validate(
                new BrokerTrustConfiguration(
                    "disabled",
                    new OpenBladeReleaseTrustPolicy(false, []))));
        Assert.Equal(OpenBladeReleaseVerificationFailure.PolicyDisabled, exception.Failure);
    }

    [Fact]
    public void ProcessPlan_HasOnlyFixedDirectArgumentsAndMinimalEnvironment()
    {
        Directory.CreateDirectory(root);
        string installer = Path.Combine(root, OpenBladeReleaseManifestCodec.InstallerFileName);
        File.WriteAllBytes(installer, [1]);
        ProcessStartInfo plan = BrokerInstallerProcessPlan.Create(root);

        Assert.False(plan.UseShellExecute);
        Assert.Equal(installer, plan.FileName);
        Assert.Equal(root, plan.WorkingDirectory);
        Assert.Equal(["/install", "/quiet", "/norestart"], plan.ArgumentList);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(windows, plan.Environment["SystemRoot"]);
        Assert.Equal(windows, plan.Environment["WINDIR"]);
        Assert.Equal(Path.Combine(windows, "System32", "cmd.exe"), plan.Environment["ComSpec"]);
        Assert.Equal($"{Path.Combine(windows, "System32")};{windows}", plan.Environment["PATH"]);
        Assert.Equal(root, plan.Environment["TEMP"]);
        Assert.Equal(root, plan.Environment["TMP"]);
        Assert.DoesNotContain(
            plan.Environment.Keys,
            BrokerInstallerProcessPlan.IsBlockedEnvironmentVariable);
    }

    [Theory]
    [InlineData("OPENBLADE_PSEUDO_LOCALIZATION")]
    [InlineData("WixDebugBootstrapperApplication")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("COREHOST_TRACE")]
    [InlineData("COMPlus_ReadyToRun")]
    [InlineData("COR_ENABLE_PROFILING")]
    [InlineData("__COMPAT_LAYER")]
    public void ProcessPlan_BlocksEnvironmentControlVariables(string name)
    {
        Assert.True(BrokerInstallerProcessPlan.IsBlockedEnvironmentVariable(name));
    }

    [Theory]
    [InlineData(true, 0, true, true, false, BrokerDeploymentDisposition.Succeeded)]
    [InlineData(true, 1, false, false, true, BrokerDeploymentDisposition.RolledBack)]
    [InlineData(false, null, false, false, false, BrokerDeploymentDisposition.Indeterminate)]
    [InlineData(true, 0, true, false, false, BrokerDeploymentDisposition.Indeterminate)]
    public void Classifier_NeverConvertsAmbiguityToSuccessOrRetry(
        bool resultKnown,
        int? exitCode,
        bool targetInstalled,
        bool healthy,
        bool previousRestored,
        BrokerDeploymentDisposition expected)
    {
        BrokerDeploymentDisposition actual = BrokerDeploymentClassifier.Classify(
            resultKnown,
            exitCode,
            targetInstalled,
            healthy,
            previousRestored);

        Assert.Equal(expected, actual);
        Assert.False(BrokerDeploymentClassifier.MayRetryAutomatically(actual));
    }

    [Fact]
    public async Task TerminalReport_IsCreateOnceAndRelaunchIsFullyBound()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerDurableRecordStore store = new(paths);
        BrokerTerminalReport report = CreateReport();

        await store.CreateTerminalReportOnceAsync(report);
        await Assert.ThrowsAsync<IOException>(() => store.CreateTerminalReportOnceAsync(report));

        Assert.True(BrokerRelaunchPolicy.MayRelaunch(
            report,
            report.RequestId,
            report.InstallationId,
            report.PackageSha256,
            clientIsNormalIntegrity: true,
            installedPayloadVerified: true,
            brokerResponseConfirmedSuccess: true));
        Assert.False(BrokerRelaunchPolicy.MayRelaunch(
            report,
            Guid.NewGuid(),
            report.InstallationId,
            report.PackageSha256,
            clientIsNormalIntegrity: true,
            installedPayloadVerified: true,
            brokerResponseConfirmedSuccess: true));
        Assert.False(BrokerRelaunchPolicy.MayRelaunch(
            report,
            report.RequestId,
            report.InstallationId,
            report.PackageSha256,
            clientIsNormalIntegrity: false,
            installedPayloadVerified: true,
            brokerResponseConfirmedSuccess: true));
        Assert.False(BrokerRelaunchPolicy.MayRelaunch(
            report,
            report.RequestId,
            report.InstallationId,
            report.PackageSha256,
            clientIsNormalIntegrity: true,
            installedPayloadVerified: true,
            brokerResponseConfirmedSuccess: false));
    }

    [Fact]
    public async Task TerminalReport_InterruptionBeforePublishLeavesNoPartialFinalOrTemp()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerDurableRecordStore store = new(
            paths,
            _ => throw new SimulatedInterruptionException());
        BrokerTerminalReport report = CreateReport();

        await Assert.ThrowsAsync<SimulatedInterruptionException>(() =>
            store.CreateTerminalReportOnceAsync(report));

        Assert.False(File.Exists(Path.Combine(
            paths.ReportDirectory,
            report.RequestId.ToString("N") + ".json")));
        Assert.Empty(Directory.GetFiles(paths.ReportDirectory, "*.tmp"));
    }

    [Fact]
    public async Task TerminalReport_CollisionNeverOverwritesExistingFinal()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerDurableRecordStore store = new(paths);
        BrokerTerminalReport original = CreateReport();
        await store.CreateTerminalReportOnceAsync(original);
        string reportPath = Path.Combine(
            paths.ReportDirectory,
            original.RequestId.ToString("N") + ".json");
        byte[] originalBytes = await File.ReadAllBytesAsync(reportPath);

        await Assert.ThrowsAsync<IOException>(() =>
            store.CreateTerminalReportOnceAsync(original with
            {
                Disposition = BrokerDeploymentDisposition.Indeterminate,
                InstallerExitCode = null,
                ServiceHealthy = false,
            }));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(reportPath));
        Assert.Empty(Directory.GetFiles(paths.ReportDirectory, "*.tmp"));
    }

    [Fact]
    public async Task TransitionRecovery_RejectsTornFinalLineAndAppendFailsClosed()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        paths.EnsureDirectory(paths.JournalDirectory);
        Guid requestId = Guid.NewGuid();
        string journal = Path.Combine(paths.JournalDirectory, requestId.ToString("N") + ".jsonl");
        BrokerTransition transition = new(
            1,
            requestId,
            BrokerTransitionKind.Accepted,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero),
            "request-accepted");
        await File.WriteAllBytesAsync(journal, BrokerTransitionCodec.SerializeCanonical(transition));
        BrokerDurableRecordStore store = new(paths);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ValidateExistingJournalsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.AppendTransitionAsync(transition with
            {
                Transition = BrokerTransitionKind.Ambiguous,
                Detail = "must-not-append",
            }));
    }

    [Fact]
    public async Task TransitionRecovery_AcceptsOnlyCompleteCanonicalLines()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerDurableRecordStore store = new(paths);
        Guid requestId = Guid.NewGuid();
        BrokerTransition transition = new(
            1,
            requestId,
            BrokerTransitionKind.Accepted,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero),
            "request-accepted");

        await store.AppendTransitionAsync(transition);
        await store.ValidateExistingJournalsAsync();
        IReadOnlyList<BrokerTransition> recovered = await store.ReadTransitionsAsync(
            Path.Combine(paths.JournalDirectory, requestId.ToString("N") + ".jsonl"));

        Assert.Equal([transition], recovered);
    }

    [Fact]
    public void StagingCleanup_RemovesOnlyExactValidatedRequestDirectory()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid requestId = Guid.NewGuid();
        string request = paths.CreateRequestStagingDirectory(requestId);
        File.WriteAllBytes(Path.Combine(request, "bounded.bin"), [1, 2, 3]);
        string unrelated = Path.Combine(paths.StagingDirectory, "unrelated-admin-evidence");
        Directory.CreateDirectory(unrelated);
        File.WriteAllBytes(Path.Combine(unrelated, "keep.bin"), [4, 5, 6]);

        paths.DeleteRequestStagingDirectory(requestId);

        Assert.False(Directory.Exists(request));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.bin")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static OpenBladeReleaseManifest CreateManifest(string version) => new()
    {
        SchemaVersion = OpenBladeReleaseManifestCodec.CurrentSchemaVersion,
        SignatureAlgorithm = OpenBladeReleaseManifestCodec.SignatureAlgorithm,
        SigningKeyCurve = OpenBladeReleaseManifestCodec.SigningKeyCurve,
        SigningKeyId = new string('a', 64),
        InstallationId = new Guid("11111111-1111-1111-1111-111111111111"),
        Sequence = 1,
        Channel = OpenBladeReleaseManifestCodec.LocalDevChannel,
        Product = OpenBladeReleaseManifestCodec.Product,
        Version = version,
        SourceRevision = new string('b', 40),
        Architecture = OpenBladeReleaseManifestCodec.Architecture,
        MinimumWindowsBuild = OpenBladeReleaseManifestCodec.MinimumWindowsBuild,
        InstallerFileName = OpenBladeReleaseManifestCodec.InstallerFileName,
        BundleId = OpenBladeReleaseManifestCodec.BundleId,
        ProviderKey = OpenBladeReleaseManifestCodec.ProviderKey,
        UpgradeCode = OpenBladeReleaseManifestCodec.UpgradeCode,
        InstallerSize = 1,
        InstallerSha256 = new string('c', 64),
    };

    internal static BrokerTerminalReport CreateReport() => new(
        1,
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        1,
        "openblade-local-dev-v1",
        BrokerDeploymentDisposition.Succeeded,
        new string('a', 64),
        new string('b', 64),
        new string('c', 64),
        "1.0.0",
        "65535.1.2.3",
        0,
        true,
        new DateTimeOffset(2026, 8, 27, 0, 1, 0, TimeSpan.Zero));

    private sealed class SimulatedInterruptionException : Exception;
}
