using OpenBlade.Contracts;
using OpenBlade.Setup;
using static OpenBlade.Setup.Tests.SetupPayloadTransactionTests;

namespace OpenBlade.Setup.Tests;

public sealed class SetupBackendCutoverTests
{
    private const string ServiceFile = "OpenBlade.Service.exe";
    private const string AgentFile = "OpenBlade.SessionAgent.exe";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundleUpgrade_RequiresCompatibleStatusAfterReplacingBothBackendArtifacts(bool compatible)
    {
        using var tree = new TestTree();
        tree.CreateCompletePayload();
        tree.WriteInstall(ServiceFile, "prior service"u8.ToArray());
        tree.WriteInstall(AgentFile, "prior agent"u8.ToArray());
        tree.WriteInstall("OpenBlade.Setup.exe", "prior uninstaller"u8.ToArray());
        var priorPayload = ReadManifest(tree.Install);
        var candidatePayload = ReadManifest(tree.Payload);
        Assert.NotEqual(priorPayload[ServiceFile], candidatePayload[ServiceFile]);
        Assert.NotEqual(priorPayload[AgentFile], candidatePayload[AgentFile]);

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

        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl: windows, shortcutWriter: windows, healthProbe: probe, processOperations: windows,
            paths: paths, payloadDirectory: tree.Payload, uninstallRegistration: windows,
            bundleOwnershipRegistration: windows, dynamicLightingIdentityRegistration: windows,
            installedUninstallerWriter: windows, progressReporter: progress, signInTaskScheduler: tasks,
            currentUserSid: "S-1-5-21-1-2-3-1001", pawnIoPrerequisite: windows);
        var runner = new SetupCommandRunner(platform, progressReporter: progress);
        string[] command = ["install", "--bundle-managed", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef"];
        if (compatible)
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
            var failure = await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(
                () => runner.RunAsync(command, CancellationToken.None));
            Assert.Equal(OpenBladeIpc.ProtocolVersion, failure.ExpectedVersion);
            Assert.Equal(OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion, failure.ActualVersion);
            Assert.Equal(2, observedRequests.Count);
            Assert.Equal(priorPayload, ReadManifest(tree.Install));
            Assert.Equal(OfflineWindows.PriorOwner, windows.Ownership);
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.SessionAgent));
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.Tray));
            Assert.Equal(agentPath, File.ReadAllText(paths.SessionAgentStartupFile));
            Assert.Equal(trayPath, File.ReadAllText(paths.StartupFile));
            Assert.DoesNotContain(progress.Events, entry => entry.Code == SetupProgressCode.CommitFiles);
            Assert.Equal(candidatePayload, windows.StoppedPayloads[1]);
        }

        foreach (var connection in observedRequests)
        {
            Assert.Equal(request, await connection.ReadWrittenRequestAsync(CancellationToken.None));
        }
        Assert.Equal(compatible ? 1 : 2, windows.StoppedPayloads.Count);
        Assert.Equal(priorPayload, windows.StoppedPayloads[0]);
        Assert.Equal(SetupServiceState.Running, windows.ServiceState);
        Assert.Equal(trayPath, File.ReadAllText(paths.StartMenuFile));
        Assert.Equal(new[] { tree.Install, sibling }.Order(), Directory.GetDirectories(tree.ProgramFiles).Order());
        Assert.Equal(siblingFiles, ReadManifest(sibling));
        Assert.Equal(machineData, ReadManifest(paths.MachineDataDirectory));
        Assert.Equal(userData, ReadManifest(paths.UserDataDirectory));
        Assert.Equal(candidatePayload, ReadManifest(tree.Payload));
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
        public SortedDictionary<string, string>? StartedPayload { get; private set; }
        public List<SortedDictionary<string, string>> StoppedPayloads { get; } = [];

        public Task<bool> ExistsAsync(string serviceName, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<SetupServiceState?> GetStateAsync(string serviceName, CancellationToken cancellationToken) =>
            Task.FromResult<SetupServiceState?>(ServiceState);

        public Task RunAsync(IReadOnlyList<string> arguments, IReadOnlyCollection<int> toleratedExitCodes,
            CancellationToken cancellationToken)
        {
            switch (arguments[0])
            {
                case "stop":
                    Assert.Equal(SetupServiceState.Running, ServiceState);
                    StoppedPayloads.Add(ReadManifest(paths.InstallDirectory));
                    ServiceState = SetupServiceState.Stopped;
                    break;
                case "config":
                    Assert.Equal($"\"{Path.Combine(paths.InstallDirectory, ServiceFile)}\"", arguments[3]);
                    break;
                case "start":
                    Assert.Equal(SetupServiceState.Stopped, ServiceState);
                    StartedPayload = ReadManifest(paths.InstallDirectory);
                    ServiceState = SetupServiceState.Running;
                    break;
                case "description" or "failure" or "failureflag":
                    break;
                default:
                    throw new NotSupportedException(arguments[0]);
            }
            return Task.CompletedTask;
        }

        public Task WaitForStateAsync(string serviceName, SetupServiceState state, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Assert.Equal(state, ServiceState);
            return Task.CompletedTask;
        }

        public Task WaitForDeletionAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName) => [];
        public bool TrySignalEvent(string eventName) => throw new NotSupportedException();
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
        public Task<bool> EnsureAsync(string stagedPayloadDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
