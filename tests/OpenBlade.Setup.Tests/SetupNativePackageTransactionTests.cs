#if OPENBLADE_NATIVE_PACKAGE_TESTS
using OpenBlade.Deployment;
using OpenBlade.Deployment.Tests;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupCommandRunnerTests
{
    [Theory]
    [InlineData((int)NativeEpochBoundary.Reserved)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    [InlineData((int)NativeEpochBoundary.EvidenceRetaining)]
    [InlineData((int)NativeEpochBoundary.EvidenceRetained)]
    public async Task NativePreparationFailureRecoversPriorLifecycleAndDisposesWithoutActivation(int boundary)
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
    public async Task PayloadFailureBeforeNativeStartupRecoversButPossibleServiceStartupForbidsFallback(bool possibleStartup)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        var payload = new NativePayload { FailActivation = !possibleStartup };
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid());
        var platform = new RecordingSetupPlatform { PayloadOverride = native, FailurePoint = possibleStartup ? "service-install" : null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.Equal(possibleStartup, native.RequiresExplicitNativeRecovery);
        Assert.Equal(possibleStartup ? 0 : 1, payload.Rollbacks);
        Assert.Equal(possibleStartup ? 0 : 1, payload.Disposals);
        Assert.Equal(possibleStartup ? NativeEpochPhase.Unresolved : NativeEpochPhase.Provisional, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
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

    private sealed class InertNativeFiles : ISetupFileOperations
    {
        public bool FileExists(string path) => false;
        public void DeleteFile(string path) { }
    }
    private sealed class InertNativeScm : ISetupServiceControl
    {
        internal List<string[]> Commands { get; } = [];
        public Task<bool> ExistsAsync(string name, CancellationToken token) => Task.FromResult(false);
        public Task<SetupServiceState?> GetStateAsync(string name, CancellationToken token) => Task.FromResult<SetupServiceState?>(SetupServiceState.Stopped);
        public Task RunAsync(IReadOnlyList<string> arguments, IReadOnlyCollection<int> codes, CancellationToken token) { Commands.Add(arguments.ToArray()); return Task.CompletedTask; }
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
        internal bool FailActivation;
        internal bool AllowCommit;
        internal Action? ObserveCommit;
        public string StagedPayloadDirectory => "inert";
        public bool HadExistingInstall => true;
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
