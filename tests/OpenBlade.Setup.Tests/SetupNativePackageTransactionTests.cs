#if OPENBLADE_NATIVE_PACKAGE_TESTS
using OpenBlade.Deployment;
using OpenBlade.Deployment.Tests;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupCommandRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncomingNonMailboxPreflightRefusesBeforeRunnerMutationsAndFreshBeginKeepsManagedRollback(bool preflight)
    {
        using var epochs = ManagedMailboxCorrectionTests.Bootstrap();
        using var handoff = ManagedMailboxCorrectionTests.Handoff();
        using var packages = await ManagedMailboxCorrectionTests.Packages();
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var transfer = ManagedMailboxCorrectionTests.SignedTransfer(handoff, packages, run, '6');
        epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("positive preparation cut"); } };
        var storage = new ManagedMailboxStorage(() => false, () => throw new Exception("no legacy root"), () => handoff.RetainFixtureView());
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: storage));
        epochs.PublishedForTests = null;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = handoff.AcquireServiceCustody(Path.Combine(handoff.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(handoff, native); }
        using var source = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var incoming = await WindowsNativeEpochTests.Package(source, nonce: 'c', schema: 2);
        var payload = new NativePayload();
        var transaction = new SetupNativePackageTransaction(payload, epochs.RetainFixtureView(), incoming, Guid.NewGuid());
        var platform = new RecordingSetupPlatform { PayloadOverride = transaction, ServiceExists = true };
        var before = Directory.EnumerateFiles(epochs.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        async Task ProgramComposition()
        {
            // Same package-aware selection call and ordering as Setup Program.
            using var selected = preflight ? await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default, incoming: incoming, storage: storage) : null;
            await new SetupCommandRunner(platform).RunAsync(["install"], default);
        }
        var failure = await Assert.ThrowsAsync<NativeRecoveryException>(ProgramComposition);
        Assert.Equal(NativeRecoveryReason.UnknownBootstrapHistory, failure.Reason);
        Assert.False(transaction.RequiresExplicitNativeRecovery); Assert.Equal(0, payload.Activations);
        if (preflight)
        { Assert.Empty(platform.Operations); Assert.Equal(0, payload.Rollbacks); }
        else
        { Assert.Contains("service-restore", platform.Operations); Assert.Equal(1, payload.Rollbacks); Assert.DoesNotContain("service-install", platform.Operations); }
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
        await transaction.DisposeAsync();
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("prerequisite")]
    [InlineData("cancel")]
    public async Task AlreadyRetiredPredecessorDoesNotRetypePreMutationFailureOrCreateEpoch(string stage)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())) { }
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { selection!.RetireRecoverableSelection(); }
        var baseline = Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var inventory = Directory.GetFileSystemEntries(files.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        Exception expected = stage == "cancel" ? new OperationCanceledException("injected pre-mutation cancellation") : new InvalidOperationException(stage);
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid());
        var platform = new RecordingSetupPlatform
        {
            PayloadOverride = native,
            CaptureFailure = stage == "capture" ? expected : null,
            PrerequisiteFailure = stage != "capture" ? expected : null,
            PredecessorKind = SetupServicePredecessorKind.Native
        };
        platform.ServiceTransition = SetupServiceTransition.NativeRetired;
        var actual = await Record.ExceptionAsync(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.Same(expected, actual);
        Assert.False(native.RequiresExplicitNativeRecovery);
        Assert.Equal(0, payload.Activations);
        Assert.Equal(1, payload.Disposals);
        Assert.DoesNotContain(platform.Operations, op => op.StartsWith("service-"));
        Assert.Equal(inventory, Directory.GetFileSystemEntries(files.Root, "*", SearchOption.AllDirectories).Order().ToArray());
        foreach (var (path, bytes) in baseline) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostRenameOrReadbackSubmissionFailureFencesRollbackBeforeExplicitStart(bool readback)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files, schema: 2);
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid());
        files.PublishedForTests = path =>
        {
            if (Path.GetFileName(path) != "submission.bin") { return; }
            if (readback) { File.WriteAllBytes(path, "torn submission readback"u8.ToArray()); }
            else { throw new IOException("submission renamed before failure"); }
        };
        var platform = new RecordingSetupPlatform { PayloadOverride = native };
        var failure = await Assert.ThrowsAsync<NativeRecoveryException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.Equal(NativeRecoveryReason.PreStartTransitionInterrupted, failure.Reason);
        Assert.True(native.RequiresExplicitNativeRecovery);
        Assert.Equal(0, payload.Rollbacks);
        Assert.Equal(0, payload.Disposals);
        Assert.DoesNotContain("service-install", platform.Operations);
        var record = NativeEpochRecord.Parse(files.ReadCurrent()!);
        Assert.Equal(NativeEpochPhase.Unresolved, record.Phase);
        Assert.True(File.Exists(Path.Combine(files.Root, record.Epoch, "submission.bin")));
        Assert.DoesNotContain("service-restore", platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualRunnerCleanupAwaitCancellationRefusesBeginWithoutOrphanOrPayloadActivation(bool retainStartup)
    {
        using var epochs = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var handoff = WindowsNativeEpochFiles.CreatePrivateFixture();
        handoff.PublishCustody(Path.Combine(handoff.Root, "exclusion.lock"), NativeCanonical.Lines("OpenBlade.MailboxExclusion.v1"));
        using var package = await WindowsNativeEpochTests.Package(epochs, schema: 2, mailbox: true);
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var owner = WindowsManagedMailboxRun.StartForTests(handoff.RetainFixtureView(), run, package.Release.Manifest.InstallationId.ToString("N"));
        using var transfer = new WindowsManagedMailboxTransfer(handoff.RetainFixtureView(), run.Encode(),
            new(NativeCanonical.Digest(run.Encode()), package.Release.Manifest.InstallationId.ToString("N"), new string('6', 32), NativeCanonical.Digest(package.Record)));
        owner.PublishQuiescent(); owner.Dispose(); transfer.ConfirmOriginalExited();
        var selected = ManagedMailboxRecovery.Selected(handoff)!;
        byte[] originalExit = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(handoff, selected, "original-exit"));
        byte[] receipt = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(handoff, selected, "receipt"));
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, epochs.RetainFixtureView(), package, Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, mailboxStorage: new(() => false, () => throw new Exception("no legacy root"), () => handoff.RetainFixtureView()));
        ManagedMailboxStartup.Admission? startup = null;
        bool scheduled = false;
        var platform = new RecordingSetupPlatform
        {
            PayloadOverride = native,
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(true),
            ObserveOperation = operation =>
            {
                if (operation != "tray-stop" || scheduled) { return; }
                scheduled = true;
                startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()), _ => null, _ => null,
                    fresh => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(handoff.RetainFixtureView(), fresh, _ => { }));
                if (!retainStartup) { startup.Dispose(); startup = null; }
            }
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new SetupCommandRunner(platform).RunAsync(["install"], default));
            Assert.True(scheduled);
            Assert.Equal(0, payload.Activations); Assert.Equal(1, payload.Rollbacks); Assert.Equal(1, payload.Disposals);
            Assert.False(native.RequiresExplicitNativeRecovery);
            Assert.DoesNotContain("service-install", platform.Operations);
            Assert.Contains("service-restore", platform.Operations);
            Assert.Equal(new[] { "owner" }, Directory.EnumerateDirectories(epochs.Root).Select(Path.GetFileName));
            Assert.Null(epochs.ReadCurrent());
            Assert.Equal(originalExit, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(handoff, selected, "original-exit")));
            Assert.Equal(receipt, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(handoff, selected, "receipt")));
            Assert.Null(ManagedMailboxRecovery.ReadOptional(handoff, ManagedMailboxRecovery.PathFor(handoff, selected, "epoch-intent")));
        }
        finally { startup?.Dispose(); await native.DisposeAsync(); }
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.Reserved)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    [InlineData((int)NativeEpochBoundary.EvidenceRetaining)]
    [InlineData((int)NativeEpochBoundary.EvidenceRetained)]
    public async Task NativePreparationFailureRestoresSafeManagedPredecessorWithoutNativeReplay(int boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid(), reached =>
        { if ((int)reached == boundary) { throw new IOException("prepare failed"); } });
        var platform = new RecordingSetupPlatform
        {
            PayloadOverride = native,
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(true)
        };
        await Assert.ThrowsAsync<IOException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.False(native.IsActivated);
        Assert.False(native.RequiresExplicitNativeRecovery);
        Assert.Equal(0, payload.Activations);
        Assert.Equal(1, payload.Rollbacks);
        Assert.Equal(1, payload.Disposals);
        Assert.Contains("service-restore", platform.Operations);
        Assert.DoesNotContain("service-install", platform.Operations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.ActivateAsync(CancellationToken.None));
        await native.DisposeAsync();
        Assert.Equal(1, payload.Disposals);
        // This is only metadata recovery. No service/process or readiness ran.
        if (boundary != (int)NativeEpochBoundary.Reserved)
        {
            string frontier = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last();
            WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, NativeCanonical.Digest(File.ReadAllBytes(frontier)));
            Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePreparationCancellationNeverActivatesOrReplaysAndAlwaysDisposes(bool afterPublication)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        using var cancel = new CancellationTokenSource();
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid(), reached =>
        { if (afterPublication && reached == NativeEpochBoundary.EvidenceRetained) { cancel.Cancel(); } });
        if (!afterPublication) { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.ActivateAsync(cancel.Token));
        Assert.False(native.RequiresExplicitNativeRecovery);
        await native.RollbackAsync(CancellationToken.None);
        await native.DisposeAsync();
        Assert.Equal(0, payload.Activations);
        Assert.Equal(1, payload.Disposals);
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.ActivateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFailureAfterRetirementNeverRestoresRetiredOrUnpreparedService(bool possibleStartup)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        var payload = new NativePayload { FailActivation = !possibleStartup };
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid());
        var platform = new RecordingSetupPlatform
        {
            PayloadOverride = native,
            FailurePoint = possibleStartup ? "service-install" : null,
            PredecessorKind = SetupServicePredecessorKind.Native,
            PreparedTransition = SetupServiceTransition.NativeRetirementPossible
        };
        await Assert.ThrowsAsync<NativeRecoveryException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.True(native.RequiresExplicitNativeRecovery);
        Assert.Equal(0, payload.Rollbacks);
        Assert.Equal(possibleStartup ? 0 : 1, payload.Disposals);
        Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        if (possibleStartup) { Assert.DoesNotContain("tray-startup-restore:Shortcut", platform.Operations); }
    }

    [Fact]
    public async Task ActualRunnerPublishesProvisionalBeforeStartAndCommitsOnlyAfterBoundReadinessAndRegistration()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        var operations = new List<string>();
        var payload = new NativePayload { AllowCommit = true };
        payload.ObserveCommit = () =>
        {
            Assert.Equal(NativeEpochPhase.Provisional, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
            operations.Add("commit");
        };
        SetupNativePackageTransaction? native = null;
        var adapters = new SetupNativeReadinessAdapters(
            _ => Task.FromResult(new SetupCandidateServiceObservation(17, true, 0x10, 2, "LocalSystem", $"\"{native!.ServicePath}\"")),
            (_, path, digest) => { var launch = new SetupCandidateLaunch(17, 1, path, digest); return (launch, new InertNativeLaunch(launch)); },
            async (candidate, token) =>
            {
                Assert.Equal(NativeEpochPhase.Provisional, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
                await candidate.VerifyLaunchAsync(token);
                operations.Add("ready");
                await candidate.VerifyLaunchAsync(token);
            });
        native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid(), readiness: adapters);
        var platform = new RecordingSetupPlatform { PayloadOverride = native };
        Assert.Equal(0, await new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.Equal(["ready", "commit"], operations);
        Assert.Contains("service-install", platform.Operations);
        Assert.Contains("agent-startup-install", platform.Operations);
        Assert.Equal(NativeEpochPhase.Committed, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        Assert.Equal(1, payload.Activations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualWindowsRegistrationSeamsSelectOnlyTheVerifiedGenerationAndKeepManagedDefault(bool selected)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        await using var native = new SetupNativePackageTransaction(new NativePayload(), files, package, Guid.NewGuid());
        var scm = new InertNativeScm();
        SignInTaskDefinition? action = null;
        var tasks = new InMemorySignInTaskScheduler { BeforeRegister = value => action = value };
        var paths = SetupPaths.Create(@"C:\Offline\Programs", @"C:\Offline\Data", @"C:\Offline\User", @"C:\Offline\Menu\Startup");
        var platform = WindowsSetupPlatformTestFactory.Create(serviceControl: scm, signInTaskScheduler: tasks,
            paths: paths, fileOperations: new InertNativeFiles(), currentUserSid: "S-1-5-21-1-2-3-1001");
        if (selected) { await native.ActivateAsync(CancellationToken.None); platform.BindSelectedNativeTransaction(native); }
        await platform.InstallServiceAsync(CancellationToken.None);
        await platform.InstallSessionAgentStartupAsync(CancellationToken.None);
        string service = selected ? native.ServicePath : Path.Combine(paths.InstallDirectory, "OpenBlade.Service.exe");
        string agent = selected ? native.AgentPath : Path.Combine(paths.InstallDirectory, "OpenBlade.SessionAgent.exe");
        Assert.Contains(scm.Commands, c => c.Contains($"\"{service}\""));
        Assert.Equal(agent, action!.ExecutablePath);
        Assert.Equal(Path.GetDirectoryName(agent), action.WorkingDirectory);
        Assert.Null(action.Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPlatformStoppedOrAbsentServiceRetiresBeforeAgentDisposalAndFreshWriter(bool absent)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        string prior;
        using (var epoch = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            prior = epoch.Record.Epoch; epoch.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(files, epoch.Record, 17, 1, true);
            epoch.CommitAfterBoundReadiness(new string('e', 64));
        }
        var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        var operations = new List<string>();
        var scm = new InertNativeScm { Exists = !absent };
        var paths = SetupPaths.Create(@"C:\Offline\Programs", @"C:\Offline\Data", @"C:\Offline\User", @"C:\Offline\Menu\Startup");
        var platform = WindowsSetupPlatformTestFactory.Create(serviceControl: scm, paths: paths,
            processOperations: new InertNativeProcesses(operations), fileOperations: new InertNativeFiles());
        platform.BindExistingNativeSelection(selection);
        if (absent) { await platform.RemoveServiceAsync(CancellationToken.None); }
        else { await platform.PrepareServiceForInstallAsync(CancellationToken.None); }
        Assert.Equal(NativeEpochPhase.Retired, selection!.Record.Phase);
        Assert.Throws<IOException>(() => files.AcquireWriter());
        await platform.StopSessionAgentAsync(CancellationToken.None);
        Assert.Equal(new[] { "OpenBlade.SessionAgent" }, operations);
        using var incoming = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.NotEqual(prior, incoming.Record.Epoch);
        Assert.Equal(2, incoming.Record.Ordinal);
    }

    [Fact]
    public async Task ActualPlatformNativePreStartFailureRefusesGenericRestoreWithoutAnyScmMutation()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        await using var native = new SetupNativePackageTransaction(new NativePayload(), files, package, Guid.NewGuid());
        native.ObservePredecessorTransition(SetupServiceTransition.NativeRetirementPossible);
        var scm = new InertNativeScm();
        var paths = SetupPaths.Create(@"C:\Offline\Programs", @"C:\Offline\Data", @"C:\Offline\User", @"C:\Offline\Menu\Startup");
        var platform = WindowsSetupPlatformTestFactory.Create(serviceControl: scm, paths: paths,
            fileOperations: new InertNativeFiles(), currentUserSid: "S-1-5-21-1-2-3-1001");
        platform.BindSelectedNativeTransaction(native);
        await Assert.ThrowsAsync<NativeRecoveryException>(() => platform.RestoreServiceInstallStateAsync(new SetupServiceInstallState(true, true), CancellationToken.None));
        Assert.Empty(scm.Commands);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ActualRunnerManagedOrFirstInstallTrayFailureRestoresOnlySafeState(bool managed, bool canceled)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        var payload = new NativePayload { ExistingInstall = managed };
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid());
        using var cancel = new CancellationTokenSource();
        var platform = new RecordingSetupPlatform
        {
            PayloadOverride = native,
            ServiceExists = managed,
            FailurePoint = canceled ? "tray-stop-cancel" : "tray-stop",
            Cancellation = cancel,
            Transaction = new RecordingPayloadTransaction(managed),
        };
        if (canceled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SetupCommandRunner(platform).RunAsync(["install"], cancel.Token)); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None)); }
        Assert.False(native.RequiresExplicitNativeRecovery);
        Assert.Equal(1, payload.Rollbacks);
        Assert.Equal(managed, platform.ServiceRunning);
        Assert.Equal(managed, platform.Operations.Contains("service-restore"));
        Assert.Contains("tray-startup-restore:Shortcut", platform.Operations);
        Assert.DoesNotContain("service-install", platform.Operations);
        Assert.Null(files.ReadCurrent());
    }

    [Theory]
    [InlineData("retired")]
    [InlineData("missing-drain")]
    [InlineData("ambiguous-publication")]
    public async Task ActualRunnerNativeRetirementRefusalOrAmbiguityNeverRestartsPredecessor(string outcome)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        NativeEpochRecord original;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, outcome != "missing-drain");
            tx.CommitAfterBoundReadiness(new string('e', 64)); original = tx.Record;
        }
        using var prior = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        if (outcome == "ambiguous-publication")
        { files.PublishedForTests = path => { if (Path.GetFileName(path) == "Retired.bin") { throw new IOException("retirement publication uncertain"); } }; }
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid());
        var platform = new RecordingSetupPlatform
        {
            PayloadOverride = native,
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(true),
            PredecessorKind = SetupServicePredecessorKind.Native,
            PreparedTransition = SetupServiceTransition.NativeRetirementPossible,
            PrepareNativeTransition = () => prior!.RetireRecoverableSelection(),
            FailurePoint = "tray-stop",
        };
        var refusal = await Assert.ThrowsAsync<NativeRecoveryException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.Equal(NativeRecoveryReason.PreStartTransitionInterrupted, refusal.Reason);
        if (outcome != "retired")
        {
            var cause = Assert.IsType<NativeRecoveryException>(refusal.InnerException);
            Assert.Equal(outcome == "missing-drain" ? NativeRecoveryReason.MissingAttemptDrain : NativeRecoveryReason.RetirementPublicationFailed, cause.Reason);
        }
        Assert.Equal(outcome == "retired" ? NativeEpochPhase.Retired : NativeEpochPhase.Committed, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        Assert.True(native.RequiresExplicitNativeRecovery);
        Assert.DoesNotContain("service-restore", platform.Operations);
        Assert.DoesNotContain("service-install", platform.Operations);
        Assert.Equal(0, payload.Rollbacks);
        Assert.Equal(0, payload.Activations);
        Assert.True(File.Exists(Path.Combine(files.Root, original.Epoch, "attempts", "launch-17-1.bin")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualWindowsManagedRestoreUsesCapturedPathDespiteIncomingNativeSelection(bool wasRunning, bool registered)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        await using var native = new SetupNativePackageTransaction(new NativePayload(), files, package, Guid.NewGuid());
        var scm = new InertNativeScm { Exists = registered };
        var paths = SetupPaths.Create(@"C:\Offline\Programs", @"C:\Offline\Data", @"C:\Offline\User", @"C:\Offline\Menu\Startup");
        SignInTaskDefinition? action = null;
        var tasks = new InMemorySignInTaskScheduler { BeforeRegister = value => action = value };
        var platform = WindowsSetupPlatformTestFactory.Create(serviceControl: scm, paths: paths, fileOperations: new InertNativeFiles(), healthProbe: new InertNativeHealth(), signInTaskScheduler: tasks);
        platform.BindSelectedNativeTransaction(native);
        string managed = Path.Combine(paths.InstallDirectory, "OpenBlade.Service.exe");
        await platform.RestoreServiceInstallStateAsync(new SetupServiceInstallState(true, wasRunning)
        { ExecutablePath = managed }, CancellationToken.None);
        Assert.Contains(scm.Commands, c => c.Contains($"\"{managed}\""));
        Assert.Equal(wasRunning, scm.Commands.Any(c => c[0] == "start"));
        Assert.DoesNotContain(scm.Commands, c => c.Any(a => a.Contains(files.Root, StringComparison.Ordinal)));
        await platform.RestoreSessionAgentStartupAsync(SetupStartupRegistration.SignInTask, CancellationToken.None);
        Assert.Equal(Path.Combine(paths.InstallDirectory, "OpenBlade.SessionAgent.exe"), action!.ExecutablePath);
        Assert.Equal(paths.InstallDirectory, action.WorkingDirectory);
    }

    [Fact]
    public async Task ActualWindowsCancellationAfterConfigurationRestoresStoppedManagedPathBeforeNativeSubmission()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        await using var native = new SetupNativePackageTransaction(new NativePayload(), files, package, Guid.NewGuid());
        await native.ActivateAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var scm = new InertNativeScm { Exists = true, BeforeRun = args => { if (args[0] == "failureflag") { cancel.Cancel(); } } };
        var paths = SetupPaths.Create(@"C:\Offline\Programs", @"C:\Offline\Data", @"C:\Offline\User", @"C:\Offline\Menu\Startup");
        var platform = WindowsSetupPlatformTestFactory.Create(serviceControl: scm, paths: paths,
            fileOperations: new InertNativeFiles(), healthProbe: new InertNativeHealth());
        platform.BindSelectedNativeTransaction(native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => platform.InstallServiceAsync(cancel.Token));
        Assert.False(native.RequiresExplicitNativeRecovery);
        Assert.DoesNotContain(scm.Commands, args => args[0] == "start");
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(native.ServicePath))!, "submission.bin")));
        await native.RollbackAsync(CancellationToken.None);
        scm.BeforeRun = null;
        string managed = Path.Combine(paths.InstallDirectory, "OpenBlade.Service.exe");
        await platform.RestoreServiceInstallStateAsync(new SetupServiceInstallState(true, false) { ExecutablePath = managed }, CancellationToken.None);
        Assert.Equal($"\"{managed}\"", scm.Commands.Last(args => args[0] == "config")[3]);
        Assert.DoesNotContain(scm.Commands, args => args[0] == "start");
    }

    private sealed class InertNativeHealth : ISetupServiceHealthProbe
    {
        public Task VerifyAsync(CancellationToken token) => Task.CompletedTask;
    }
    private sealed class InertNativeProcesses(List<string> operations) : ISetupProcessOperations
    {
        public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName) { operations.Add(processName); return []; }
        public bool TrySignalEvent(string name) => throw new InvalidOperationException("no actual processes");
    }

    private sealed class InertNativeFiles : ISetupFileOperations
    {
        public bool FileExists(string path) => false;
        public void DeleteFile(string path) { }
    }
    private sealed class InertNativeScm : ISetupServiceControl
    {
        internal List<string[]> Commands { get; } = [];
        internal bool Exists;
        internal Action<IReadOnlyList<string>>? BeforeRun;
        public Task<bool> ExistsAsync(string name, CancellationToken token) => Task.FromResult(Exists);
        public Task<SetupServiceState?> GetStateAsync(string name, CancellationToken token) => Task.FromResult<SetupServiceState?>(Exists ? SetupServiceState.Stopped : null);
        public Task RunAsync(IReadOnlyList<string> arguments, IReadOnlyCollection<int> codes, CancellationToken token) { Commands.Add(arguments.ToArray()); BeforeRun?.Invoke(arguments); return Task.CompletedTask; }
        public Task WaitForStateAsync(string name, SetupServiceState state, TimeSpan timeout, CancellationToken token) => Task.CompletedTask;
        public Task WaitForDeletionAsync(string name, TimeSpan timeout, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class InertNativeLaunch(SetupCandidateLaunch launch) : ISetupRetainedCandidateLaunch
    {
        public ValueTask<SetupCandidateLaunch?> ReadLiveAsync(CancellationToken token) => ValueTask.FromResult<SetupCandidateLaunch?>(launch);
    }

    private sealed class NativePayload : ISetupPayloadTransaction
    {
        internal int Activations, Rollbacks, Disposals;
        internal bool ExistingInstall = true;
        internal bool FailActivation;
        internal bool AllowCommit;
        internal Action? ObserveCommit;
        public string StagedPayloadDirectory => "inert";
        public bool HadExistingInstall => ExistingInstall;
        public bool IsActivated { get; private set; }
        public string? CommitWarning => null;
        public Task ActivateAsync(CancellationToken cancellationToken)
        {
            Activations++; IsActivated = true;
            if (FailActivation) { throw new InvalidOperationException("payload activation ambiguous"); }
            return Task.CompletedTask;
        }
        public Task CommitAsync(CancellationToken cancellationToken) { if (!AllowCommit) { throw new InvalidOperationException("no readiness fixture"); } ObserveCommit?.Invoke(); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken) { Rollbacks++; IsActivated = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
#endif
