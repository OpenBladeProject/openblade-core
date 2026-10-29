using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class ApplyFixedInboxBrokerRequestHandlerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.DeploymentBroker.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HandleAsync_SuccessUsesOnlyVerifiedFixedProcessPlan()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false));

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Succeeded, response.Status);
        ProcessStartInfo plan = Assert.IsType<ProcessStartInfo>(fixture.Runner.CapturedPlan);
        Assert.False(plan.UseShellExecute);
        Assert.Equal(
            Path.Combine(
                fixture.Paths.StagingDirectory,
                fixture.Request.RequestId.ToString("N"),
                OpenBladeReleaseManifestCodec.InstallerFileName),
            plan.FileName);
        Assert.Equal(["/install", "/quiet", "/norestart", "/log", Path.Combine(plan.WorkingDirectory, "OpenBlade-Install.log")], plan.ArgumentList);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(windows, plan.Environment["SystemRoot"]);
        Assert.Equal(windows, plan.Environment["WINDIR"]);
        Assert.Equal(Path.Combine(windows, "System32", "cmd.exe"), plan.Environment["ComSpec"]);
        Assert.Equal($"{Path.Combine(windows, "System32")};{windows}", plan.Environment["PATH"]);
        Assert.Equal(plan.WorkingDirectory, plan.Environment["TEMP"]);
        Assert.Equal(plan.WorkingDirectory, plan.Environment["TMP"]);
        Assert.DoesNotContain(
            plan.Environment.Keys,
            BrokerInstallerProcessPlan.IsBlockedEnvironmentVariable);
        Assert.Single(Directory.GetFiles(fixture.Paths.ReportDirectory, "*.json"));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Paths.StagingDirectory,
            fixture.Request.RequestId.ToString("N"))));
        Assert.False(fixture.Inbox.TokenCanBeCanceled);
        Assert.Equal(1, fixture.Sequence.Consumed);
    }

    [Fact]
    public async Task HandleAsync_RollbackRetainsNestedInstallerLogsBeforeCleanup()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 1, false),
            new BrokerPostInstallVerification(false, true, true));
        fixture.Runner.BeforeResult = plan =>
        {
            Assert.Equal("/log", plan.ArgumentList[3]);
            File.WriteAllText(plan.ArgumentList[4], "burn failure details");
            string nested = Path.Combine(plan.WorkingDirectory, "burn-temp", ".be");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "setup.log"), "original installer cause");
        };

        BrokerResponse response = await fixture.Handler.HandleAsync(fixture.Request, fixture.Enrollment, CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.RolledBack, response.Status);
        Assert.Equal("install-rolled-back", response.Code);
        string archive = Path.Combine(fixture.Paths.InstallerLogDirectory, fixture.Request.RequestId.ToString("N"));
        string[] retained = await Task.WhenAll(Directory.GetFiles(archive, "*.log").Select(log => File.ReadAllTextAsync(log)));
        Assert.Equal(2, retained.Length);
        Assert.Contains("burn failure details", retained);
        Assert.Contains("original installer cause", retained);
        Assert.False(Directory.Exists(fixture.Runner.CapturedPlan!.WorkingDirectory));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
        Assert.Contains("installer-logs-retained", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(fixture.Paths.JournalDirectory))));
    }

    [Theory]
    [InlineData(0, BrokerResponseStatus.Succeeded)]
    [InlineData(1, BrokerResponseStatus.RolledBack)]
    public async Task HandleAsync_DiagnosticFailurePreservesOutcomeAndOriginalStaging(int exitCode, BrokerResponseStatus expected)
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, exitCode, false),
            new BrokerPostInstallVerification(exitCode == 0, true, exitCode != 0));
        FileStream? lockedLog = null;
        fixture.Runner.BeforeResult = plan =>
        {
            string log = Path.Combine(plan.WorkingDirectory, "setup.log");
            File.WriteAllText(log, "preserve this failure");
            lockedLog = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None);
        };
        BrokerResponse response;
        try
        {
            response = await fixture.Handler.HandleAsync(fixture.Request, fixture.Enrollment, CancellationToken.None);
        }
        finally
        {
            lockedLog?.Dispose();
        }
        Assert.Equal(expected, response.Status);
        Assert.Equal(exitCode == 0 ? "install-succeeded" : "install-rolled-back", response.Code);
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
        Assert.Equal("preserve this failure", await File.ReadAllTextAsync(Path.Combine(fixture.Runner.CapturedPlan!.WorkingDirectory, "setup.log")));
        Assert.Contains("installer-logs-unavailable", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(fixture.Paths.JournalDirectory))));
        Assert.Contains(exitCode == 0 ? "Succeeded" : "RolledBack", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(fixture.Paths.ReportDirectory))));
    }

    [Fact]
    public async Task HandleAsync_FreshSequenceAllowsExactOlderSemanticVersionForLocalDevelopment()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false),
            installedVersion: "3.0.0");

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Succeeded, response.Status);
        Assert.Equal(1, fixture.Sequence.Consumed);
    }

    [Fact]
    public async Task HandleAsync_EnrollmentKeyMismatchNeverStagesOrExecutes()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false),
            enrollmentKeyMismatch: true);

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Rejected, response.Status);
        Assert.Equal("local-dev-trust-invalid", response.Code);
        Assert.Equal(0, fixture.Inbox.CallCount);
        Assert.Equal(0, fixture.Runner.CallCount);
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
    }

    [Fact]
    public async Task HandleAsync_ReplayedSequenceNeverExecutes()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false),
            acceptedSequence: 1);

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Indeterminate, response.Status);
        Assert.Equal("install-indeterminate", response.Code);
        Assert.Equal(1, fixture.Inbox.CallCount);
        Assert.Equal(0, fixture.Runner.CallCount);
        Assert.True(File.Exists(fixture.Paths.InFlightPath));

    }

    [Fact]
    public async Task HandleAsync_DownloaderCannotRedirectExecutionOutsideRequestStaging()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false),
            returnUnauthorizedInstallerPath: true);

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Rejected, response.Status);
        Assert.Equal("preflight-rejected", response.Code);
        Assert.Equal(0, fixture.Runner.CallCount);
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Paths.StagingDirectory,
            fixture.Request.RequestId.ToString("N"))));
    }

    [Fact]
    public async Task HandleAsync_IndependentlyRejectsDownloaderWithInvalidSignature()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false),
            corruptDetachedSignature: true);

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Rejected, response.Status);
        Assert.Equal("preflight-rejected", response.Code);
        Assert.Equal(0, fixture.Runner.CallCount);
    }

    [Fact]
    public async Task HandleAsync_PostCommitResultBindingFailureLeavesRecoveryUnpoisoned()
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false),
            afterInFlightReplacement: state =>
            {
                if (state.InstallerExitCode.HasValue)
                {
                    throw new IOException("Injected post-commit latch verification failure.");
                }
            });

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Indeterminate, response.Status);
        Assert.Equal("installer-result-unresolved", response.Code);
        BrokerInFlightDeployment unresolved = Assert.IsType<BrokerInFlightDeployment>(
            await new BrokerInFlightStore(fixture.Paths).LoadAsync());
        Assert.Equal(0, unresolved.InstallerExitCode);
        Assert.False(Directory.Exists(fixture.Paths.ReportDirectory));

        BrokerStartupReconciler reconciler = new(
            new BrokerInFlightStore(fixture.Paths),
            new NotRunningProcessInspector(),
            fixture.State,
            new BrokerDurableRecordStore(fixture.Paths),
            fixture.Paths,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 0, 1, 0, TimeSpan.Zero)),
            NullLogger<BrokerStartupReconciler>.Instance);

        Assert.True(await reconciler.TryReconcileAsync(
            fixture.Enrollment,
            CancellationToken.None));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
        string reportPath = Assert.Single(Directory.GetFiles(fixture.Paths.ReportDirectory, "*.json"));
        Assert.Contains("\"installerExitCode\":0", await File.ReadAllTextAsync(reportPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_ProcessBindingFailureRetainsActualCommitStateAndFailsClosed(bool afterCommit)
    {
        static void FailProcessBinding(BrokerInFlightDeployment state)
        {
            if (state.InstallerProcessId.HasValue && !state.InstallerExitCode.HasValue)
            {
                throw new IOException("Injected PID binding failure.");
            }
        }

        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 0, false),
            new BrokerPostInstallVerification(true, true, false),
            afterInFlightReplacement: afterCommit ? FailProcessBinding : null,
            beforeInFlightReplacement: afterCommit ? null : FailProcessBinding);

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request, fixture.Enrollment, CancellationToken.None);

        Assert.Equal(BrokerResponseStatus.Indeterminate, response.Status);
        Assert.Equal("installer-process-unresolved", response.Code);
        BrokerInFlightDeployment unresolved = Assert.IsType<BrokerInFlightDeployment>(
            await new BrokerInFlightStore(fixture.Paths).LoadAsync());
        Assert.Equal(afterCommit ? 4242 : null, unresolved.InstallerProcessId);
        Assert.Equal(afterCommit ? 0 : null, unresolved.InstallerExitCode);
        Assert.False(Directory.Exists(fixture.Paths.ReportDirectory));
        IReadOnlyList<BrokerTransition> transitions = await new BrokerDurableRecordStore(fixture.Paths)
            .ReadTransitionsAsync(Path.Combine(
                fixture.Paths.JournalDirectory, fixture.Request.RequestId.ToString("N") + ".jsonl"));
        Assert.Equal(
            afterCommit ? BrokerTransitionKind.InstallerExited : BrokerTransitionKind.Ambiguous,
            transitions[^1].Transition);
        Assert.Equal(
            afterCommit
                ? "installer-exit-observed"
                : "installer-exit-observed-process-binding-unresolved",
            transitions[^1].Detail);

        BrokerResponse repeated = await fixture.Handler.HandleAsync(
            fixture.Request with { RequestId = Guid.NewGuid() },
            fixture.Enrollment, CancellationToken.None);
        Assert.Equal("unresolved-deployment", repeated.Code);
        Assert.Equal(1, fixture.Runner.CallCount);

        BrokerStartupReconciler reconciler = new(
            new BrokerInFlightStore(fixture.Paths),
            new NotRunningProcessInspector(), fixture.State,
            new BrokerDurableRecordStore(fixture.Paths), fixture.Paths,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 0, 1, 0, TimeSpan.Zero)),
            NullLogger<BrokerStartupReconciler>.Instance);
        Assert.Equal(afterCommit, await reconciler.TryReconcileAsync(
            fixture.Enrollment, CancellationToken.None));
        Assert.Equal(!afterCommit, File.Exists(fixture.Paths.InFlightPath));
    }

    [Theory]
    [InlineData(false, null, false, false, false, BrokerResponseStatus.Indeterminate)]
    [InlineData(true, 1, false, true, true, BrokerResponseStatus.RolledBack)]
    [InlineData(true, 0, true, false, false, BrokerResponseStatus.Indeterminate)]
    public async Task HandleAsync_ClassifiesAmbiguousAndRollbackWithoutRetry(
        bool exitObserved,
        int? exitCode,
        bool targetInstalled,
        bool serviceHealthy,
        bool previousRestored,
        BrokerResponseStatus expected)
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(
                exitObserved,
                exitCode,
                TimedOut: !exitObserved),
            new BrokerPostInstallVerification(
                targetInstalled,
                serviceHealthy,
                previousRestored));

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(expected, response.Status);
        Assert.Equal(1, fixture.Runner.CallCount);
        if (expected == BrokerResponseStatus.Indeterminate)
        {
            Assert.True(File.Exists(fixture.Paths.InFlightPath));
            BrokerInFlightDeployment unresolved = Assert.IsType<BrokerInFlightDeployment>(
                await new BrokerInFlightStore(fixture.Paths).LoadAsync());
            Assert.Equal(exitObserved ? exitCode : null, unresolved.InstallerExitCode);
            BrokerResponse repeated = await fixture.Handler.HandleAsync(
                fixture.Request with { RequestId = Guid.NewGuid() },
                fixture.Enrollment,
                CancellationToken.None);
            Assert.Equal(BrokerResponseStatus.Indeterminate, repeated.Status);
            Assert.Equal("unresolved-deployment", repeated.Code);
            Assert.Equal(1, fixture.Runner.CallCount);
        }
        else
        {
            Assert.False(File.Exists(fixture.Paths.InFlightPath));
        }
    }

    [Theory]
    [InlineData(true, BrokerResponseStatus.RolledBack)]
    [InlineData(false, BrokerResponseStatus.Indeterminate)]
    public async Task HandleAsync_FirstInstallRollbackRequiresVerifiedAbsentState(
        bool previousRestored,
        BrokerResponseStatus expectedStatus)
    {
        using HandlerFixture fixture = CreateFixture(
            new BrokerInstallerExecutionResult(true, 1603, false),
            new BrokerPostInstallVerification(
                ExactTargetInstalled: false,
                ServiceHealthy: false,
                ExactPreviousStateRestored: previousRestored),
            before: BrokerInstalledState.NotInstalled);

        BrokerResponse response = await fixture.Handler.HandleAsync(
            fixture.Request,
            fixture.Enrollment,
            CancellationToken.None);

        Assert.Equal(expectedStatus, response.Status);
        Assert.Equal(1, fixture.Runner.CallCount);
        Assert.Equal(
            expectedStatus == BrokerResponseStatus.Indeterminate,
            File.Exists(fixture.Paths.InFlightPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private HandlerFixture CreateFixture(
        BrokerInstallerExecutionResult execution,
        BrokerPostInstallVerification postInstall,
        bool enrollmentKeyMismatch = false,
        long? acceptedSequence = null,
        string installedVersion = "1.0.0",
        bool returnUnauthorizedInstallerPath = false,
        bool corruptDetachedSignature = false,
        Action<BrokerInFlightDeployment>? afterInFlightReplacement = null,
        BrokerInstalledState? before = null,
        Action<BrokerInFlightDeployment>? beforeInFlightReplacement = null)
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        string keyId = OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(spki);
        Guid installationId = Guid.NewGuid();
        byte[] installer = "verified OpenBlade installer"u8.ToArray();
        OpenBladeReleaseManifest manifest = DeploymentPolicyTests.CreateManifest("65535.1.2.3") with
        {
            SigningKeyId = keyId,
            InstallationId = installationId,
            InstallerSize = installer.Length,
            InstallerSha256 = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(),
        };
        byte[] manifestBytes = OpenBladeReleaseManifestCodec.SerializeCanonical(manifest);
        byte[] signatureBytes = key.SignHash(
            OpenBladeSignedReleaseVerifier.ComputeSigningDigest(manifestBytes),
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (corruptDetachedSignature)
        {
            signatureBytes[0] ^= 0xFF;
        }
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        FakeSequenceStore sequenceStore = new(acceptedSequence);
        FakeDownloader downloader = new(
            installer,
            returnUnauthorizedInstallerPath,
            new StagedVerifiedLocalDevDeployment(
                new OpenBladeVerifiedRelease(manifest, keyId),
                manifestBytes,
                signatureBytes,
                string.Empty));
        FakeRunner runner = new(execution);
        FakeInstalledStateVerifier state = new(
            before ?? new BrokerInstalledState(
                    installedVersion,
                    installedVersion + "+" + new string('d', 40),
                    new string('e', 64)),
            postInstall);
        DateTimeOffset now = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        ApplyFixedInboxBrokerRequestHandler handler = new(
            downloader,
            sequenceStore,
            new BrokerDurableRecordStore(paths),
            new BrokerInFlightStore(paths, afterInFlightReplacement, beforeInFlightReplacement),
            state,
            runner,
            paths,
            new FixedTimeProvider(now),
            NullLogger<ApplyFixedInboxBrokerRequestHandler>.Instance);
        Guid requestId = Guid.NewGuid();
        BrokerEnrollment enrollment = BrokerEnrollmentTests.CreateEnrollment() with
        {
            InstallationId = installationId,
            DevelopmentSigningKeyId = enrollmentKeyMismatch ? new string('0', 64) : keyId,
            DevelopmentSigningSubjectPublicKeyInfoBase64 = Convert.ToBase64String(spki),
        };
        return new HandlerFixture(
            key,
            handler,
            paths,
            downloader,
            sequenceStore,
            runner,
            state,
            new BrokerRequest(
                BrokerRequestCodec.ProtocolVersion,
                requestId,
                BrokerAction.ApplyFixedInbox),
            enrollment);
    }

    private sealed class FakeDownloader(
        byte[] installerBytes,
        bool returnUnauthorizedInstallerPath,
        StagedVerifiedLocalDevDeployment release) : IFixedLocalDevInboxSource
    {
        public int CallCount { get; private set; }
        public bool TokenCanBeCanceled { get; private set; }

        public async Task<StagedVerifiedLocalDevDeployment> StageAsync(
            string stagingDirectory,
            OpenBladeReleaseTrustPolicy trustPolicy,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            TokenCanBeCanceled = cancellationToken.CanBeCanceled;
            string installerPath = Path.Combine(
                stagingDirectory,
                OpenBladeReleaseManifestCodec.InstallerFileName);
            await File.WriteAllBytesAsync(installerPath, installerBytes, cancellationToken);
            return release with
            {
                InstallerPath = returnUnauthorizedInstallerPath
                    ? Path.Combine(Path.GetTempPath(), "unauthorized-OpenBlade-Setup.exe")
                    : installerPath,
            };
        }

    }

    private sealed class FakeSequenceStore(long? consumed = null) : IBrokerAcceptedSequenceStore
    {
        public long? Consumed { get; private set; } = consumed;

        public Task ConsumeAsync(long sequence, CancellationToken cancellationToken = default)
        {
            if (Consumed.HasValue && sequence <= Consumed.Value)
            {
                throw new InvalidOperationException("sequence replay");
            }

            Consumed = sequence;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner(BrokerInstallerExecutionResult result)
        : IBrokerInstallerProcessRunner
    {
        public int CallCount { get; private set; }
        public ProcessStartInfo? CapturedPlan { get; private set; }
        public Action<ProcessStartInfo>? BeforeResult { get; set; }

        public Task<BrokerInstallerExecutionResult> RunAsync(
            ProcessStartInfo startInfo,
            Action<int> processStarted,
            CancellationToken cancellationToken)
        {
            CallCount++;
            CapturedPlan = startInfo;
            BeforeResult?.Invoke(startInfo);
            try
            {
                processStarted(4242);
                return Task.FromResult(result);
            }
            catch (Exception exception)
            {
                return Task.FromResult(result with { ProcessBindingFailure = exception });
            }
        }
    }

    private sealed class FakeInstalledStateVerifier(
        BrokerInstalledState before,
        BrokerPostInstallVerification after) : IBrokerInstalledStateVerifier
    {
        public Task<BrokerInstalledState> ReadBeforeInstallAsync(
            CancellationToken cancellationToken) => Task.FromResult(before);

        public Task<BrokerPostInstallVerification> VerifyAfterInstallAsync(
            BrokerInstalledState ignoredBefore,
            OpenBladeVerifiedRelease expectedRelease,
            CancellationToken cancellationToken) => Task.FromResult(after);

        public Task<BrokerPostInstallVerification> VerifyCurrentStateAsync(
            BrokerInstalledState ignoredBefore,
            string expectedVersion,
            string expectedSourceRevision,
            CancellationToken cancellationToken) => Task.FromResult(after);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NotRunningProcessInspector : IBrokerRecordedInstallerProcessInspector
    {
        public Task<BrokerRecordedInstallerProcessStatus> InspectAsync(
            BrokerInFlightDeployment deployment,
            CancellationToken cancellationToken) => Task.FromResult(
                BrokerRecordedInstallerProcessStatus.NotRunning);
    }

    private sealed record HandlerFixture(
        ECDsa Key,
        ApplyFixedInboxBrokerRequestHandler Handler,
        BrokerPaths Paths,
        FakeDownloader Inbox,
        FakeSequenceStore Sequence,
        FakeRunner Runner,
        FakeInstalledStateVerifier State,
        BrokerRequest Request,
        BrokerEnrollment Enrollment) : IDisposable
    {
        public void Dispose() => Key.Dispose();
    }
}
