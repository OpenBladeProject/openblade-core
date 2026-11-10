using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.Setup;
using static OpenBlade.Setup.Tests.SetupPayloadTransactionTests;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupBackendCutoverTests
{
    internal const string LiveFixtureDirectoryVariable = "OPENBLADE_PHASE_7C_FIXTURES";

    // Separate, frozen local builds of the reviewed fixture entry points. Neither
    // the caller's environment nor the staged payload can select the expected hashes.
    private const string LiveServiceFixture = "openblade-windows-host.exe"; // 1ec18766f3afdeeb819d07c7f91771d5da9c44e4
    private const string LiveServiceHash = "18782F1B760CA2F983D7547785F4C1D6E8C3977D576B74B361EA417478AF2555";
    private const string LiveAgentFixture = "openblade-rust-session-agent.exe"; // 7e4781de5144bcc72a16dd3d195057a5db878c8a
    private const string LiveAgentHash = "4B2F551C3AC495DC3CE5AD583D79443CC9CEB167F18EA034ED7DCC291AC10E51";

    private const string AgentRefusal = "GPU activity data is unavailable.";

    [LocalLiveRustFixtureTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundleUpgrade_RequiresBothPrivateRustPeersBeforeCommit(bool refuseAgent)
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.False(identity.IsSystem);
        Assert.False(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));

        using var tree = new TestTree();
        string expectedRoot = Path.Combine(Path.GetTempPath(), "OpenBlade.Setup.Tests", Path.GetFileName(tree.Root));
        Assert.Equal(Path.GetFullPath(expectedRoot), tree.Root);
        Assert.True(Guid.TryParseExact(Path.GetFileName(tree.Root), "N", out _));
        Assert.Equal(Path.Combine(tree.Root, "ProgramFiles", "OpenBlade"), tree.Install);
        RequireUnlinkedPath(tree.Root);

        tree.CreateCompletePayload();
        foreach (string file in ReadManifest(tree.Payload).Keys)
        {
            tree.WriteInstall(file, Encoding.UTF8.GetBytes($"prior managed {file}"));
        }
        tree.WriteInstall("OpenBlade.Setup.exe", "prior uninstaller"u8.ToArray());
        var prior = ReadManifest(tree.Install);

        string source = Environment.GetEnvironmentVariable(LiveFixtureDirectoryVariable)!;
        Assert.True(Path.IsPathFullyQualified(source));
        File.Copy(Path.Combine(source, LiveServiceFixture), Path.Combine(tree.Payload, ServiceFile), overwrite: true);
        File.Copy(Path.Combine(source, LiveAgentFixture), Path.Combine(tree.Payload, AgentFile), overwrite: true);
        var candidate = ReadManifest(tree.Payload);
        VerifyLiveRoles(tree.Payload, candidate);

        SetupPaths paths = tree.Paths;
        string agentPath = Path.Combine(tree.Install, AgentFile);
        string trayPath = Path.Combine(tree.Install, "OpenBlade.Tray.exe");
        File.WriteAllText(paths.StartMenuFile, trayPath);
        Directory.CreateDirectory(paths.CommonStartupRoot);
        File.WriteAllText(paths.SessionAgentStartupFile, agentPath);
        File.WriteAllText(paths.StartupFile, trayPath);

        var windows = new OfflineWindows(paths);
        var tasks = new InMemorySignInTaskScheduler();
        using var progress = new RecordingSetupProgressReporter();
        using var rehearsalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string childDirectory = Path.Combine(tree.Root, "private-fixtures");
        Directory.CreateDirectory(childDirectory);
        await using var children = new PrivateFixturePair(tree.Install, childDirectory);
        SortedDictionary<string, string>? staged = null;
        bool candidateHealthEntered = false;
        bool serviceReady = false;
        SessionGpuResponse? agentResponse = null;
        int rollbackHealthChecks = 0;
        int candidateStops = 0;

        void AssertCandidateBeforeCommit()
        {
            Assert.Equal(candidate, staged);
            Assert.Equal(candidate, windows.StartedPayload);
            Assert.Equal(candidate, ReadManifest(tree.Install));
            string backup = Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
            Assert.Equal(prior, ReadManifest(backup));
            Assert.Equal(OfflineWindows.PriorOwner, windows.Ownership);
            Assert.DoesNotContain(progress.Events, entry => entry.Code == SetupProgressCode.CommitFiles);
        }

        windows.InspectStagedPayload = directory =>
        {
            Assert.Equal(tree.ProgramFiles, Path.GetDirectoryName(directory));
            Assert.NotEqual(tree.Install, directory);
            RequireUnlinkedPath(directory);
            Assert.Equal(prior, ReadManifest(tree.Install));
            staged = ReadManifest(directory);
            VerifyLiveRoles(directory, staged);
            Assert.Equal(candidate, staged);
        };
        windows.BeforeStopAsync = async () =>
        {
            if (children.Started)
            {
                await children.StopAsync();
                Assert.True(children.AllStartedExited);
                AssertCandidateBeforeCommit();
                candidateStops++;
            }
        };
        tasks.BeforeRegister = definition =>
        {
            if (definition.TaskName == SignInTaskNames.SessionAgent)
            {
                AssertCandidateBeforeCommit();
                Assert.True(serviceReady);
                Assert.Equal(SessionGpuOperationStatus.Succeeded, agentResponse?.Status);
                Assert.Equal(agentPath, definition.ExecutablePath);
                Assert.Equal(tree.Install, definition.WorkingDirectory);
            }
        };

        var health = new RehearsalHealthProbe(async (token, timing) =>
        {
            if (candidateHealthEntered)
            {
                // Setup's rollback probes the prior managed tree. It stays
                // simulated, and may only run after the retained children exit.
                Assert.True(children.AllStartedExited);
                Assert.Equal(children.Started ? 1 : 0, candidateStops);
                Assert.Equal(prior, ReadManifest(tree.Install));
                Assert.Equal(prior, windows.StartedPayload);
                rollbackHealthChecks++;
                GetStatusRequest request = NewStatusRequest();
                var restoredProbe = new SetupServiceHealthProbe(
                    token => SimulatePriorManagedStatusAsync(request, token), () => request);
                await restoredProbe.VerifyAsync(token, timing);
                return;
            }

            candidateHealthEntered = true;
            AssertCandidateBeforeCommit();
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.SessionAgent));
            VerifyLiveRoles(tree.Install, candidate);
            token.ThrowIfCancellationRequested();
            children.Start(refuseAgent);
            var serviceProbe = new SetupServiceHealthProbe(children.ConnectServiceAsync, NewStatusRequest);
            await serviceProbe.VerifyAsync(token, timing);
            serviceReady = true;

            agentResponse = await children.QueryAgentAsync(token);
            if (agentResponse.Status != SessionGpuOperationStatus.Succeeded)
            {
                throw new InvalidDataException(agentResponse.Error ?? "Session Agent readiness failed.");
            }

            Assert.Null(agentResponse.Error);
            Assert.Equal(new[] { "active-game", "idle-holder", "other-session" },
                agentResponse.Apps.Select(app => app.Name));
            Assert.All(agentResponse.Apps, app => Assert.False(app.CanClose));
        });

        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl: windows, shortcutWriter: windows, healthProbe: health, processOperations: windows,
            paths: paths, payloadDirectory: tree.Payload, uninstallRegistration: windows,
            bundleOwnershipRegistration: windows, installedUninstallerWriter: windows,
            dynamicLightingIdentityRegistration: windows, progressReporter: progress, signInTaskScheduler: tasks,
            currentUserSid: "S-1-5-21-1-2-3-1001", pawnIoPrerequisite: windows);
        var runner = new SetupCommandRunner(platform, progressReporter: progress);
        string[] command = ["install", "--bundle-managed", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef"];

        if (refuseAgent)
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(
                () => runner.RunAsync(command, rehearsalDeadline.Token));
            Assert.Equal(AgentRefusal, failure.Message);
            Assert.True(children.AllStartedExited);
            Assert.Equal(1, candidateStops);
            Assert.Equal(1, rollbackHealthChecks);
            Assert.Equal(SessionGpuOperationStatus.Failed, agentResponse?.Status);
            Assert.Empty(agentResponse!.Apps);
            Assert.Equal(prior, ReadManifest(tree.Install));
            Assert.Equal(prior, windows.StoppedPayloads[0]);
            Assert.Equal(candidate, windows.StoppedPayloads[1]);
            Assert.Equal(2, windows.StoppedPayloads.Count);
            Assert.Equal(OfflineWindows.PriorOwner, windows.Ownership);
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.SessionAgent));
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.Tray));
            Assert.Equal(agentPath, File.ReadAllText(paths.SessionAgentStartupFile));
            Assert.Equal(trayPath, File.ReadAllText(paths.StartupFile));
            Assert.DoesNotContain(progress.Events, entry => entry.Code == SetupProgressCode.CommitFiles);
        }
        else
        {
            Assert.Equal(0, await runner.RunAsync(command, rehearsalDeadline.Token));
            Assert.True(children.BothRunning);
            await children.StopAsync();
            Assert.Equal(0, candidateStops);
            Assert.Equal(0, rollbackHealthChecks);
            Assert.Equal(candidate, ReadManifest(tree.Install));
            Assert.Equal(prior, Assert.Single(windows.StoppedPayloads));
            Assert.Equal(OfflineWindows.CandidateOwner, windows.Ownership);
            Assert.Equal(SignInTaskState.Enabled, tasks.GetState(SignInTaskNames.SessionAgent));
            Assert.Equal(SignInTaskState.Enabled, tasks.GetState(SignInTaskNames.Tray));
            Assert.Contains(progress.Events, entry => entry.Code == SetupProgressCode.CommitFiles);
        }

        Assert.True(serviceReady);
        Assert.Empty(agentResponse!.Results);
        Assert.True(children.BothExitedCleanly);
        Assert.Equal(trayPath, File.ReadAllText(paths.StartMenuFile));
        Assert.Equal(tree.Install, Assert.Single(Directory.GetDirectories(tree.ProgramFiles)));
        Assert.Equal(candidate, ReadManifest(tree.Payload));
    }

    private static GetStatusRequest NewStatusRequest() => new(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());

    private static async ValueTask<Stream> SimulatePriorManagedStatusAsync(
        GetStatusRequest request, CancellationToken token)
    {
        using var response = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            response, SetupServiceHealthProbeTests.CreateStatusResponse(request), token);
        return new SetupServiceHealthProbeTests.TestDuplexStream(new MemoryStream(response.ToArray()));
    }

    private static void VerifyLiveRoles(string directory, IReadOnlyDictionary<string, string> inventory)
    {
        VerifyRustRole(directory, inventory, ServiceFile, LiveServiceHash);
        VerifyRustRole(directory, inventory, AgentFile, LiveAgentHash);
    }

    private static void RequireUnlinkedPath(string path)
    {
        for (string? component = path; component is not null; component = Path.GetDirectoryName(component))
        {
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The fixture path crosses a reparse point.");
            }
        }
    }

    private sealed class RehearsalHealthProbe(
        Func<CancellationToken, SetupServiceHealthTiming?, Task> verify) : ISetupServiceHealthProbe
    {
        public Task VerifyAsync(CancellationToken cancellationToken) => verify(cancellationToken, null);

        Task ISetupServiceHealthProbe.VerifyWithTimingAsync(
            CancellationToken cancellationToken, SetupServiceHealthTiming timing) => verify(cancellationToken, timing);
    }

    private sealed class PrivateFixturePair(string install, string scratch) : IAsyncDisposable
    {
        private const string ServicePrefix = "OpenBlade.Rust.ReadOnly.Dev.";
        private const string AgentPrefix = "OpenBlade.Rust.Session.Gpu.Dev.";
        private readonly string serviceId = Guid.NewGuid().ToString("N");
        private readonly string agentId = Guid.NewGuid().ToString("N");
        private Process? service;
        private Process? agent;
        private bool stopped;

        public bool Started => service is not null;
        public bool BothRunning => service is { HasExited: false } && agent is { HasExited: false };
        public bool AllStartedExited => (service is null || service.HasExited) && (agent is null || agent.HasExited);
        public bool BothExitedCleanly => service is { HasExited: true, ExitCode: 0 }
            && agent is { HasExited: true, ExitCode: 0 };

        public void Start(bool refuseAgent)
        {
            Assert.False(Started);
            Assert.NotEqual(serviceId, agentId);
            RequireUnlinkedPath(scratch);
            string servicePath = Path.Combine(scratch, LiveServiceFixture);
            string agentPath = Path.Combine(scratch, LiveAgentFixture);
            // Launch under development names, using only bytes copied from the
            // activated candidate. Keep both copies locked through process creation.
            using FileStream serviceBytes = CopyPinned(Path.Combine(install, ServiceFile), servicePath, LiveServiceHash);
            using FileStream agentBytes = CopyPinned(Path.Combine(install, AgentFile), agentPath, LiveAgentHash);

            var serviceStart = NewStartInfo(servicePath);
            serviceStart.ArgumentList.Add("foreground");
            serviceStart.ArgumentList.Add(serviceId);
            var agentStart = NewStartInfo(agentPath);
            agentStart.ArgumentList.Add("gpu-foreground");
            agentStart.ArgumentList.Add(agentId);
            agentStart.ArgumentList.Add(refuseAgent ? "no-data" : "available");
            service = Process.Start(serviceStart) ?? throw new InvalidOperationException("Could not start the service fixture.");
            agent = Process.Start(agentStart) ?? throw new InvalidOperationException("Could not start the Session Agent fixture.");
        }

        public async ValueTask<Stream> ConnectServiceAsync(CancellationToken token)
            => await ConnectAsync(service!, ServicePrefix + serviceId, token);

        public async Task<SessionGpuResponse> QueryAgentAsync(CancellationToken token)
        {
            await using var pipe = await ConnectAsync(agent!, AgentPrefix + agentId, token);
            var request = new QueryGpuAppsRequest(SessionGpuIpc.ProtocolVersion, Guid.NewGuid());
            await SessionGpuIpcCodec.WriteRequestAsync(pipe, request, token);
            SessionGpuResponse response = await SessionGpuIpcCodec.ReadResponseAsync(pipe, token);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(request.Version, response.Version);
            return response;
        }

        private ProcessStartInfo NewStartInfo(string path)
        {
            var start = new ProcessStartInfo(path)
            {
                WorkingDirectory = scratch,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            // Do not inherit product config, search paths, or fixture overrides.
            // Both selected modes are in-memory; even temp paths are confined.
            start.Environment.Clear();
            start.Environment["SystemRoot"] = Path.GetDirectoryName(Environment.SystemDirectory)!;
            start.Environment["TEMP"] = scratch;
            start.Environment["TMP"] = scratch;
            return start;
        }

        private static FileStream CopyPinned(string activatedPath, string privatePath, string hash)
        {
            using FileStream activated = OpenPinned(activatedPath, hash);
            using (FileStream copy = File.Open(privatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                activated.Position = 0;
                activated.CopyTo(copy);
            }
            return OpenPinned(privatePath, hash);
        }

        private static FileStream OpenPinned(string path, string hash)
        {
            RequireUnlinkedPath(path);
            FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(file)));
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private static async Task<NamedPipeClientStream> ConnectAsync(Process child, string name, CancellationToken token)
        {
            // READY is only a startup barrier. Readiness itself requires a typed
            // response on a local pipe whose server is this retained process.
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync(token));
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(token);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                Assert.False(child.HasExited);
                Assert.Equal((uint)child.Id, peer);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (stopped)
            {
                return;
            }
            await Task.WhenAll(StopAsync(service), StopAsync(agent));
            stopped = true;
        }

        private static async Task StopAsync(Process? child)
        {
            if (child is null || child.HasExited)
            {
                return;
            }
            child.StandardInput.Close();
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                agent?.Dispose();
                service?.Dispose();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    }
}

internal sealed class LocalLiveRustFixtureTheoryAttribute : TheoryAttribute
{
    public LocalLiveRustFixtureTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SetupBackendCutoverTests.LiveFixtureDirectoryVariable)))
        {
            Skip = "Requires Windows and OPENBLADE_PHASE_7C_FIXTURES containing the two pinned private Rust fixtures.";
        }
    }
}
