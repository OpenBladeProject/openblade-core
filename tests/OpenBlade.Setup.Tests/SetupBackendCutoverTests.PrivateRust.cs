using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
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
    internal const string LiveFixtureDirectoryVariable = "OPENBLADE_PHASE_7E_FIXTURES";

    // Both roles were built from the same Rust revision with default features.
    // Neither the caller's environment nor the signed archive selects the pins.
    private const string LiveFixtureRevision = "41a3237e81188ed0780e89a2ca2d170da7d61db0";
    private const string LiveServiceFixture = "openblade-windows-host.exe";
    private const string LiveServiceHash = "1D63C481D9238799ABE81130B6C8B558F167F902129E9E1B3091E562C8067595";
    private const string LiveAgentFixture = "openblade-rust-session-agent.exe";
    private const string LiveAgentHash = "92A31A44138E617CABCC6D7620EDD146116DF9CEBAD01DD500FC1919C18E11C4";

    private const string AgentRefusal = "The GPU inventory scan timed out.";

    [LocalLiveRustFixtureTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundleUpgrade_RequiresSignedServiceAndUnifiedAgentReadinessBeforeCommit(bool refuseAgent)
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

        var fixture = CreateSignedFixture(SignedFixtureFault.None);
        var candidate = await StageSignedFixtureAsync(fixture, tree.Payload);
        foreach (string file in candidate.Keys)
        {
            tree.WriteInstall(file, Encoding.UTF8.GetBytes($"prior managed {file}"));
        }
        tree.WriteInstall("OpenBlade.Setup.exe", "prior uninstaller"u8.ToArray());
        var prior = ReadManifest(tree.Install);

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
        using var rehearsalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string childDirectory = Path.Combine(tree.Root, "private-fixtures");
        Directory.CreateDirectory(childDirectory);
        await using var children = new PrivateFixturePair(tree.Install, childDirectory);
        SortedDictionary<string, string>? staged = null;
        bool candidateStarted = false;
        bool candidateHealthEntered = false;
        bool serviceReady = false;
        bool displayReady = false;
        bool reporterRoundTrip = false;
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
        windows.AfterStartAsync = async token =>
        {
            // The simulated start step owns the foreground startup barriers.
            // Setup's ten-second health window covers only the typed probes.
            windows.AfterStartAsync = null;
            AssertCandidateBeforeCommit();
            Assert.Equal(SignInTaskState.Missing, tasks.GetState(SignInTaskNames.SessionAgent));
            VerifyLiveRoles(tree.Install, candidate);
            token.ThrowIfCancellationRequested();
            await children.StartAsync(refuseAgent, token);
            candidateStarted = true;
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
                Assert.True(displayReady);
                Assert.True(reporterRoundTrip);
                Assert.Equal(SessionGpuOperationStatus.Succeeded, agentResponse?.Status);
                Assert.Equal(agentPath, definition.ExecutablePath);
                Assert.Equal(tree.Install, definition.WorkingDirectory);
            }
        };

        var health = new RehearsalHealthProbe(async (token, timing) =>
        {
            if (!candidateStarted || candidateHealthEntered)
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
            Assert.Equal(79, OpenBladeIpc.ProtocolVersion);
            var serviceProbe = new SetupServiceHealthProbe(children.ConnectServiceAsync, NewStatusRequest);
            await serviceProbe.VerifyAsync(token, timing);
            serviceReady = true;

            var display = await children.QueryDisplayAsync(token);
            Assert.Equal(SessionDisplayOperationStatus.Succeeded, display.Status);
            Assert.Null(display.Error);
            Assert.True(display.State.IsAvailable);
            Assert.Equal("Synthetic Factory.icm", display.State.ActiveColorProfileId);
            displayReady = true;

            agentResponse = await children.QueryGpuAsync(token);
            if (agentResponse.Status != SessionGpuOperationStatus.Succeeded)
            {
                throw new InvalidDataException(agentResponse.Error ?? "Session Agent readiness failed.");
            }

            Assert.Null(agentResponse.Error);
            Assert.Equal(new[] { "active-game", "idle-holder", "other-session" },
                agentResponse.Apps.Select(app => app.Name));
            Assert.All(agentResponse.Apps, app => Assert.False(app.CanClose));

            var baseline = await children.ReadServiceStatusAsync(token);
            Guid profile = Assert.Single(baseline.Profiles.ApplicationProfiles).Id;
            Assert.Null(baseline.ActiveApplicationProfileId);
            await children.ReportFocusAsync(true, token);
            await children.WaitForProfileAsync(profile, token);
            await children.ReportFocusAsync(false, token);
            await children.WaitForProfileAsync(null, token);
            reporterRoundTrip = true;
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
        Assert.True(displayReady);
        Assert.Equal(!refuseAgent, reporterRoundTrip);
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
        private const string DisplayPrefix = "OpenBlade.Rust.Session.Display.Dev.";
        private const string GpuPrefix = "OpenBlade.Rust.Session.Gpu.Dev.";
        private readonly string serviceId = Guid.NewGuid().ToString("N");
        private readonly string agentId = Guid.NewGuid().ToString("N");
        private Process? service;
        private Process? agent;
        private string? serviceRoot;
        private SafeFileHandle? serviceRootPin;
        private bool serviceBarrierPassed;
        private bool serviceReportedStopped;
        private bool stopped;

        public bool Started => service is not null;
        public bool BothRunning => service is { HasExited: false } && agent is { HasExited: false };
        public bool AllStartedExited => (service is null || service.HasExited) && (agent is null || agent.HasExited);
        public bool BothExitedCleanly => service is { HasExited: true, ExitCode: 0 }
            && serviceReportedStopped && agent is { HasExited: true, ExitCode: 0 };

        public async Task StartAsync(bool refuseAgent, CancellationToken token)
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
            serviceStart.ArgumentList.Add("foreground-integrated-application");
            serviceStart.ArgumentList.Add(serviceId);
            service = Process.Start(serviceStart) ?? throw new InvalidOperationException("Could not start the service fixture.");
            Assert.Equal("START_PENDING", await service.StandardOutput.ReadLineAsync(token));
            string? root = await service.StandardOutput.ReadLineAsync(token);
            Assert.NotNull(root);
            Assert.Matches("^ROOT [0-9a-f]{32} [0-9a-f]{24}$", root);
            PinServiceRoot(root.Split(' ')[1]);
            await ExpectReadyAsync(service, token);
            serviceBarrierPassed = true;

            var agentStart = NewStartInfo(agentPath);
            agentStart.ArgumentList.Add("integrated");
            agentStart.ArgumentList.Add(agentId);
            agentStart.ArgumentList.Add("new");
            agentStart.ArgumentList.Add(refuseAgent ? "stalled" : "available");
            agentStart.ArgumentList.Add(serviceId);
            agentStart.ArgumentList.Add(service.Id.ToString(CultureInfo.InvariantCulture));
            agentStart.ArgumentList.Add(Guid.NewGuid().ToString("N"));
            agent = Process.Start(agentStart) ?? throw new InvalidOperationException("Could not start the Session Agent fixture.");
            await ExpectReadyAsync(agent, token);
        }

        public async ValueTask<Stream> ConnectServiceAsync(CancellationToken token)
            => await ConnectAsync(service!, ServicePrefix + serviceId, token);

        public async Task<SessionDisplayResponse> QueryDisplayAsync(CancellationToken token)
        {
            await using var pipe = await ConnectAsync(agent!, DisplayPrefix + agentId, token);
            var request = new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, Guid.NewGuid());
            await SessionDisplayIpcCodec.WriteRequestAsync(pipe, request, token);
            var response = await SessionDisplayIpcCodec.ReadResponseAsync(pipe, token);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(request.Version, response.Version);
            return response;
        }

        public async Task<SessionGpuResponse> QueryGpuAsync(CancellationToken token)
        {
            await using var pipe = await ConnectAsync(agent!, GpuPrefix + agentId, token);
            var request = new QueryGpuAppsRequest(SessionGpuIpc.ProtocolVersion, Guid.NewGuid());
            await SessionGpuIpcCodec.WriteRequestAsync(pipe, request, token);
            SessionGpuResponse response = await SessionGpuIpcCodec.ReadResponseAsync(pipe, token);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(request.Version, response.Version);
            return response;
        }

        public async Task<ServiceStatusSnapshot> ReadServiceStatusAsync(CancellationToken token)
        {
            await using var pipe = await ConnectServiceAsync(token);
            GetStatusRequest request = NewStatusRequest();
            await IpcMessageCodec.WriteRequestAsync(pipe, request, token);
            var response = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(pipe, token));
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(request.Version, response.Version);
            Assert.True(response.Success, response.Error);
            return Assert.IsType<ServiceStatusSnapshot>(response.Status);
        }

        public async Task WaitForProfileAsync(Guid? expected, CancellationToken token)
        {
            while ((await ReadServiceStatusAsync(token)).ActiveApplicationProfileId != expected)
            {
                await Task.Delay(25, token);
            }
        }

        public Task ReportFocusAsync(bool active, CancellationToken token)
            => SendCommandAsync(agent!, active ? (byte)'a' : (byte)'c', token);

        private static async Task SendCommandAsync(Process child, byte command, CancellationToken token)
        {
            Stream input = child.StandardInput.BaseStream;
            await input.WriteAsync(new byte[] { command }, token);
            await input.FlushAsync(token);
        }

        private void PinServiceRoot(string id)
        {
            // The existing service mode hands off a random, plain directory in
            // the current user's known-folder Temp. The Agent journal uses scratch.
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", $"openblade-durable-host-{id}");
            RequireUnlinkedPath(root);
            serviceRootPin = OpenDirectory(root, 0x80000000, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
                0x02200000, IntPtr.Zero); // Backup semantics and open reparse point.
            if (serviceRootPin.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            serviceRoot = root;
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

        private static async Task ExpectReadyAsync(Process child, CancellationToken token)
        {
            Assert.False(child.SafeHandle.IsInvalid);
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync(token));
        }

        private static async Task<NamedPipeClientStream> ConnectAsync(Process child, string name, CancellationToken token)
        {
            // Every typed query uses the PID of the still-live retained child,
            // including subsequent connections to the same endpoint.
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
            try
            {
                await StopAsync(agent, stopService: false);
            }
            finally
            {
                await StopAsync(service, stopService: true);
            }
            stopped = true;
        }

        private async Task StopAsync(Process? child, bool stopService)
        {
            if (child is null || child.HasExited)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            CancellationToken token = timeout.Token;
            try
            {
                if (stopService)
                {
                    await SendCommandAsync(child, (byte)'s', token);
                    bool pending = serviceBarrierPassed
                        && await child.StandardOutput.ReadLineAsync(token) == "STOP_PENDING";
                    await SendCommandAsync(child, (byte)'r', token);
                    serviceReportedStopped = pending
                        && await child.StandardOutput.ReadLineAsync(token) == "STOPPED";
                }

                child.StandardInput.Close();
                await child.WaitForExitAsync(token);
            }
            catch (Exception failure) when (failure is OperationCanceledException or IOException)
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
                bool exited = AllStartedExited;
                agent?.Dispose();
                service?.Dispose();
                try
                {
                    if (serviceRoot is not null && exited)
                    {
                        foreach (string file in Directory.EnumerateFiles(serviceRoot))
                        {
                            RequireUnlinkedPath(file);
                            File.Delete(file);
                        }
                        serviceRootPin?.Dispose();
                        Directory.Delete(serviceRoot);
                    }
                }
                finally
                {
                    serviceRootPin?.Dispose();
                }
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenDirectory(
            string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

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
            Skip = "Requires Windows and OPENBLADE_PHASE_7E_FIXTURES containing the two pinned integrated Rust fixtures.";
        }
    }
}
