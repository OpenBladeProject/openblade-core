using System.Reflection.PortableExecutable;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Setup;
using static OpenBlade.Setup.Tests.SetupPayloadTransactionTests;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupBackendCutoverTests
{
    private const string ServiceFile = "OpenBlade.Service.exe";
    private const string AgentFile = "OpenBlade.SessionAgent.exe";

    internal const string FixtureDirectoryVariable = "OPENBLADE_PHASE_7B_FIXTURES";

    // These pins identify the locally verified build outputs. The caller supplies
    // only a directory, and the payload's own inventory cannot authorize a role.
    // Phase 5j, 87c3eead825f52f59554fadc7eeb80696fae493a.
    private const string ServiceFixture = "openblade-windows-scm-vm.exe";
    private const string ServiceSha256 = "0EAACBAAF40FAF1AD027614D11D1A0AEF34147B2EF36CB513DA56E228B68C414";
    // Phase 6o, 7e4781de5144bcc72a16dd3d195057a5db878c8a.
    private const string AgentFixture = "openblade-rust-session-agent.exe";
    private const string AgentSha256 = "C55CFB6C34B10751D97113F422A23E35070490E2558276E2E7AB3A1A7074403B";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BundleUpgrade_RequiresCompatibleStatusAfterReplacingBothBackendArtifacts(bool compatible) =>
        RehearseBundleUpgradeAsync(compatible, null);

    [LocalRustFixtureTheory]
    [InlineData(RustOutcome.Ready)]
    [InlineData(RustOutcome.ServiceNotReady)]
    [InlineData(RustOutcome.AgentNotReady)]
    [InlineData(RustOutcome.WrongAgentRole)]
    [InlineData(RustOutcome.MissingAgentRole)]
    public Task BundleUpgrade_RehearsesPinnedRustBackends(RustOutcome outcome) =>
        RehearseBundleUpgradeAsync(outcome != RustOutcome.ServiceNotReady, outcome);

    public enum RustOutcome
    {
        Ready,
        ServiceNotReady,
        AgentNotReady,
        WrongAgentRole,
        MissingAgentRole,
    }

    private static async Task RehearseBundleUpgradeAsync(bool compatible, RustOutcome? rustOutcome)
    {
        using var tree = new TestTree();
        tree.CreateCompletePayload();
        if (rustOutcome.HasValue)
        {
            // Keep a full prior managed-shaped tree with distinct sidecar bytes,
            // so rollback checks more than the two replaced executables.
            foreach (string file in ReadManifest(tree.Payload).Keys)
            {
                tree.WriteInstall(file, Encoding.UTF8.GetBytes($"prior managed {file}"));
            }

            string fixtureDirectory = Environment.GetEnvironmentVariable(FixtureDirectoryVariable)!;
            File.Copy(Path.Combine(fixtureDirectory, ServiceFixture), Path.Combine(tree.Payload, ServiceFile), overwrite: true);
            File.Copy(Path.Combine(fixtureDirectory, AgentFixture), Path.Combine(tree.Payload, AgentFile), overwrite: true);
        }
        tree.WriteInstall(ServiceFile, "prior service"u8.ToArray());
        tree.WriteInstall(AgentFile, "prior agent"u8.ToArray());
        tree.WriteInstall("OpenBlade.Setup.exe", "prior uninstaller"u8.ToArray());
        var priorPayload = ReadManifest(tree.Install);
        var candidatePayload = ReadManifest(tree.Payload);
        Assert.NotEqual(priorPayload[ServiceFile], candidatePayload[ServiceFile]);
        Assert.NotEqual(priorPayload[AgentFile], candidatePayload[AgentFile]);
        if (rustOutcome.HasValue)
        {
            VerifyRustRoles(tree.Payload, candidatePayload);
        }

        SetupPaths paths = tree.Paths;
        WriteFile(Path.Combine(paths.MachineDataDirectory, "profiles.json"), "saved profiles\r\n"u8.ToArray());
        WriteFile(Path.Combine(paths.MachineDataDirectory, "hyperboost-ownership.json"), "recovery journal\r\n"u8.ToArray());
        var machineData = ReadManifest(paths.MachineDataDirectory);
        WriteFile(Path.Combine(paths.UserDataDirectory, "snaptap-windows-ownership.json"), "agent recovery\r\n"u8.ToArray());
        var userData = ReadManifest(paths.UserDataDirectory);
        string sibling = Path.Combine(tree.ProgramFiles, "OpenBlade-unrelated");
        WriteFile(Path.Combine(sibling, "keep"), [0x00, 0xFF]);
        var siblingFiles = ReadManifest(sibling);

        // A prior release used shortcuts. The new payload would use sign-in tasks.
        string agentPath = Path.Combine(tree.Install, AgentFile);
        string trayPath = Path.Combine(tree.Install, "OpenBlade.Tray.exe");
        File.WriteAllText(paths.StartMenuFile, trayPath);
        Directory.CreateDirectory(paths.CommonStartupRoot);
        File.WriteAllText(paths.SessionAgentStartupFile, agentPath);
        File.WriteAllText(paths.StartupFile, trayPath);

        var windows = new OfflineWindows(paths);
        var tasks = new InMemorySignInTaskScheduler();
        using var progress = new RecordingSetupProgressReporter();
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        var observedRequests = new List<SetupServiceHealthProbeTests.TestDuplexStream>();
        var probe = new SetupServiceHealthProbe(async token =>
        {
            Assert.Equal(SetupServiceState.Running, windows.ServiceState);
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.SessionAgent));
            Assert.False(File.Exists(paths.SessionAgentStartupFile));
            Assert.Equal(OfflineWindows.PriorOwner, windows.Ownership);
            bool checkingCandidate = observedRequests.Count == 0;
            Assert.Equal(checkingCandidate ? candidatePayload : priorPayload, windows.StartedPayload);
            Assert.Equal(windows.StartedPayload, ReadManifest(tree.Install));
            if (checkingCandidate)
            {
                string backup = Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
                Assert.Equal(priorPayload, ReadManifest(backup));
            }

            ServiceResponse frame = SetupServiceHealthProbeTests.CreateStatusResponse(request);
            using var response = new MemoryStream();
            if (checkingCandidate && !compatible)
            {
                // Generate the real legacy wire format. Setup uses the strict current reader.
                await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(response, frame, token);
            }
            else
            {
                await IpcMessageCodec.WriteServerFrameAsync(response, frame, token);
            }
            var connection = new SetupServiceHealthProbeTests.TestDuplexStream(new MemoryStream(response.ToArray()));
            observedRequests.Add(connection);
            return connection;
        }, () => request);

        bool agentReadinessObserved = false;
        bool agentReady = false;
        SortedDictionary<string, string>? stagedInventory = null;
        var stoppedCandidate = candidatePayload;
        if (rustOutcome.HasValue)
        {
            windows.InspectStagedPayload = directory =>
            {
                Assert.Equal(tree.ProgramFiles, Path.GetDirectoryName(directory));
                Assert.NotEqual(tree.Install, directory);
                Assert.Empty(windows.StoppedPayloads);
                Assert.Equal(priorPayload, ReadManifest(tree.Install));
                stagedInventory = ReadManifest(directory);
                VerifyRustRoles(directory, stagedInventory);
                Assert.Equal(candidatePayload, stagedInventory);
            };
            tasks.BeforeRegister = definition =>
            {
                if (definition.TaskName != SignInTaskNames.SessionAgent)
                {
                    return;
                }

                // This hook simulates readiness after Setup's typed service probe
                // returned successfully and before candidate ownership or commit.
                Assert.False(agentReadinessObserved);
                agentReadinessObserved = true;
                Assert.Single(observedRequests);
                Assert.Equal(OfflineWindows.PriorOwner, windows.Ownership);
                Assert.Equal(stagedInventory, windows.StartedPayload);
                Assert.Equal(candidatePayload, ReadManifest(tree.Install));
                Assert.Equal(agentPath, definition.ExecutablePath);
                Assert.Equal(tree.Install, definition.WorkingDirectory);
                Assert.DoesNotContain(progress.Events, entry => entry.Code == SetupProgressCode.CommitFiles);

                // Inject post-swap damage only in the test tree, with the backup
                // still present, to exercise rollback after either role failure.
                if (rustOutcome == RustOutcome.WrongAgentRole)
                {
                    File.Copy(Path.Combine(tree.Install, ServiceFile), agentPath, overwrite: true);
                }
                else if (rustOutcome == RustOutcome.MissingAgentRole)
                {
                    File.Delete(agentPath);
                }

                stoppedCandidate = ReadManifest(tree.Install);
                VerifyRustRoles(tree.Install, stoppedCandidate);
                if (rustOutcome == RustOutcome.AgentNotReady)
                {
                    throw new InvalidDataException("Simulated Session Agent readiness failed.");
                }

                agentReady = true;
            };
        }

        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl: windows, shortcutWriter: windows, healthProbe: probe, processOperations: windows,
            paths: paths, payloadDirectory: tree.Payload, uninstallRegistration: windows,
            bundleOwnershipRegistration: windows, dynamicLightingIdentityRegistration: windows,
            installedUninstallerWriter: windows, progressReporter: progress, signInTaskScheduler: tasks,
            currentUserSid: "S-1-5-21-1-2-3-1001", pawnIoPrerequisite: windows);
        var runner = new SetupCommandRunner(platform, progressReporter: progress);
        string[] command = ["install", "--bundle-managed", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef"];
        bool succeeds = compatible && (rustOutcome is null or RustOutcome.Ready);
        if (succeeds)
        {
            Assert.Equal(0, await runner.RunAsync(command, CancellationToken.None));
            Assert.Single(observedRequests);
            Assert.Equal(candidatePayload, ReadManifest(tree.Install));
            Assert.Equal(OfflineWindows.CandidateOwner, windows.Ownership);
            Assert.Equal(SignInTaskState.Enabled, tasks.GetState(SignInTaskNames.SessionAgent));
            Assert.Equal(SignInTaskState.Enabled, tasks.GetState(SignInTaskNames.Tray));
            Assert.False(File.Exists(paths.SessionAgentStartupFile));
            Assert.False(File.Exists(paths.StartupFile));
            Assert.Contains(progress.Events, entry => entry.Code == SetupProgressCode.CommitFiles);
        }
        else
        {
            if (!compatible)
            {
                var failure = await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(
                    () => runner.RunAsync(command, CancellationToken.None));
                Assert.Equal(OpenBladeIpc.ProtocolVersion, failure.ExpectedVersion);
                Assert.Equal(OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion, failure.ActualVersion);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<InvalidDataException>(
                    () => runner.RunAsync(command, CancellationToken.None));
                Assert.Equal(rustOutcome switch
                {
                    RustOutcome.WrongAgentRole => $"Unexpected SHA-256 for {AgentFile}.",
                    RustOutcome.MissingAgentRole => $"Missing backend role {AgentFile}.",
                    RustOutcome.AgentNotReady => "Simulated Session Agent readiness failed.",
                    _ => throw new InvalidOperationException("Unexpected rehearsal outcome."),
                }, failure.Message);
            }
            Assert.Equal(2, observedRequests.Count);
            Assert.Equal(priorPayload, ReadManifest(tree.Install));
            Assert.Equal(OfflineWindows.PriorOwner, windows.Ownership);
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.SessionAgent));
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.Tray));
            Assert.Equal(agentPath, File.ReadAllText(paths.SessionAgentStartupFile));
            Assert.Equal(trayPath, File.ReadAllText(paths.StartupFile));
            Assert.DoesNotContain(progress.Events, entry => entry.Code == SetupProgressCode.CommitFiles);
            Assert.Equal(stoppedCandidate, windows.StoppedPayloads[1]);
        }

        foreach (var connection in observedRequests)
        {
            Assert.Equal(request, await connection.ReadWrittenRequestAsync(CancellationToken.None));
        }
        Assert.Equal(succeeds ? 1 : 2, windows.StoppedPayloads.Count);
        if (rustOutcome.HasValue)
        {
            Assert.Equal(candidatePayload, stagedInventory);
            Assert.Equal(compatible, agentReadinessObserved);
            Assert.Equal(succeeds, agentReady);
        }
        Assert.Equal(priorPayload, windows.StoppedPayloads[0]);
        Assert.Equal(SetupServiceState.Running, windows.ServiceState);
        Assert.Equal(trayPath, File.ReadAllText(paths.StartMenuFile));
        Assert.Equal(new[] { tree.Install, sibling }.Order(), Directory.GetDirectories(tree.ProgramFiles).Order());
        Assert.Equal(siblingFiles, ReadManifest(sibling));
        Assert.Equal(machineData, ReadManifest(paths.MachineDataDirectory));
        Assert.Equal(userData, ReadManifest(paths.UserDataDirectory));
        Assert.Equal(candidatePayload, ReadManifest(tree.Payload));
    }

    private static void VerifyRustRoles(string root, IReadOnlyDictionary<string, string> inventory)
    {
        VerifyRustRole(root, inventory, ServiceFile, ServiceSha256);
        VerifyRustRole(root, inventory, AgentFile, AgentSha256);
    }

    private static void VerifyRustRole(
        string root, IReadOnlyDictionary<string, string> inventory, string fileName, string expectedHash)
    {
        RequireRustRoleHash(inventory, fileName, expectedHash);

        using var reader = new PEReader(File.OpenRead(Path.Combine(root, fileName)));
        Assert.Equal(Machine.Amd64, reader.PEHeaders.CoffHeader.Machine);
        Assert.True(reader.PEHeaders.IsExe);
        Assert.False(reader.HasMetadata);
        Assert.Equal(PEMagic.PE32Plus, reader.PEHeaders.PEHeader?.Magic);
        Assert.True(reader.PEHeaders.PEHeader?.AddressOfEntryPoint > 0);
    }

    private static void RequireRustRoleHash(
        IReadOnlyDictionary<string, string> inventory, string fileName, string expectedHash)
    {
        if (!inventory.TryGetValue(fileName, out string? actualHash))
        {
            throw new InvalidDataException($"Missing backend role {fileName}.");
        }
        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected SHA-256 for {fileName}.");
        }
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // All native operations are intercepted. Start captures what the fake SCM
    // would execute at that instant, before Setup gets any status response.
    private sealed class OfflineWindows(SetupPaths paths) : ISetupServiceControl, ISetupProcessOperations,
        IStartupShortcutWriter, IUninstallRegistration, IBundleOwnershipRegistration,
        IDynamicLightingIdentityRegistration, IInstalledUninstallerWriter, IPawnIoPrerequisite
    {
        public static readonly BundleOwnershipIdentity PriorOwner = new("WiX Burn", "0.1.0", "prior");
        public static readonly BundleOwnershipIdentity CandidateOwner = new("WiX Burn", "0.2.0", "candidate");
        public BundleOwnershipIdentity? Ownership { get; private set; } = PriorOwner;
        public SetupServiceState ServiceState { get; private set; } = SetupServiceState.Running;
        public Action<string>? InspectStagedPayload { get; set; }
        public Func<Task>? BeforeStopAsync { get; set; }
        public Action? BeforeWaitForStop { get; set; }
        public Action? BeforeStart { get; set; }
        public Func<CancellationToken, Task>? AfterStartAsync { get; set; }
        public SortedDictionary<string, string>? StartedPayload { get; private set; }
        public List<SortedDictionary<string, string>> StoppedPayloads { get; } = [];

        public Task<bool> ExistsAsync(string serviceName, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<SetupServiceState?> GetStateAsync(string serviceName, CancellationToken cancellationToken) =>
            Task.FromResult<SetupServiceState?>(ServiceState);

        public async Task RunAsync(IReadOnlyList<string> arguments, IReadOnlyCollection<int> toleratedExitCodes,
            CancellationToken cancellationToken)
        {
            switch (arguments[0])
            {
                case "stop":
                    Assert.Equal(SetupServiceState.Running, ServiceState);
                    if (BeforeStopAsync is not null)
                    {
                        await BeforeStopAsync();
                    }
                    StoppedPayloads.Add(ReadManifest(paths.InstallDirectory));
                    ServiceState = SetupServiceState.Stopped;
                    break;
                case "config":
                    Assert.Equal($"\"{Path.Combine(paths.InstallDirectory, ServiceFile)}\"", arguments[3]);
                    break;
                case "start":
                    Assert.Equal(SetupServiceState.Stopped, ServiceState);
                    BeforeStart?.Invoke();
                    StartedPayload = ReadManifest(paths.InstallDirectory);
                    ServiceState = SetupServiceState.Running;
                    if (AfterStartAsync is not null)
                    {
                        await AfterStartAsync(cancellationToken);
                    }
                    break;
                case "description" or "failure" or "failureflag":
                    break;
                default:
                    throw new NotSupportedException(arguments[0]);
            }
        }

        public Task WaitForStateAsync(string serviceName, SetupServiceState state, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (state == SetupServiceState.Stopped)
            {
                BeforeWaitForStop?.Invoke();
            }
            Assert.Equal(state, ServiceState);
            return Task.CompletedTask;
        }

        public Task WaitForDeletionAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName) => [];
        // Service quiescence binds the running SCM PID to the installed image
        // before stop and waits for that process. The fake exits on stop.
        private const int ServiceProcessId = 4242;
        public Task<SetupServiceStatus?> GetStatusAsync(string serviceName, CancellationToken cancellationToken) =>
            Task.FromResult<SetupServiceStatus?>(new(ServiceState,
                ServiceState == SetupServiceState.Running ? ServiceProcessId : null));
        public ISetupProcess GetProcessById(int processId) =>
            processId == ServiceProcessId
                ? new StoppedServiceProcess(this, Path.Combine(paths.InstallDirectory, ServiceFile))
                : throw new ArgumentException("No such fake process.", nameof(processId));
        public bool TrySignalEvent(string eventName) => throw new NotSupportedException();

        private sealed class StoppedServiceProcess(OfflineWindows owner, string executablePath) : ISetupProcess
        {
            public int Id => ServiceProcessId;
            public bool TryRetainLifetime() => true;
            public string? ExecutablePath => executablePath;
            public Task WaitForExitAsync(CancellationToken cancellationToken)
            {
                Assert.Equal(SetupServiceState.Stopped, owner.ServiceState);
                return Task.CompletedTask;
            }
            public void Dispose() { }
        }
        public Task CreateAsync(ShortcutRequest request, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(request.ShortcutPath, request.TargetPath, cancellationToken);
        public bool Exists() => false;
        public void Install(UninstallRegistrationRequest request) => throw new NotSupportedException();
        void IUninstallRegistration.Remove() { }
        public BundleOwnershipIdentity? Capture() => Ownership;
        public bool IsOwnedByCurrentBuild(BundleOwnershipIdentity ownership) => ownership == CandidateOwner;
        public void Install() => Ownership = CandidateOwner;
        public void Restore(BundleOwnershipIdentity ownership) => Ownership = ownership;
        void IBundleOwnershipRegistration.Remove() => Ownership = null;
        public Task<bool> IsRegisteredAsync(string installDirectory, string userSid, CancellationToken cancellationToken) =>
            Task.FromResult(false);
        public Task InstallIfPresentAsync(string installDirectory, string userSid, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task RemoveIfPresentAsync(string installDirectory, string userSid, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task WriteVerifiedAsync(InstalledUninstallerWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> EnsureAsync(string stagedPayloadDirectory, CancellationToken cancellationToken)
        {
            InspectStagedPayload?.Invoke(stagedPayloadDirectory);
            return Task.FromResult(false);
        }
    }
}

internal sealed class LocalRustFixtureTheoryAttribute : TheoryAttribute
{
    public LocalRustFixtureTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SetupBackendCutoverTests.FixtureDirectoryVariable)))
        {
            Skip = "Set OPENBLADE_PHASE_7B_FIXTURES to the local directory containing the two pinned Rust executables.";
        }
    }
}
