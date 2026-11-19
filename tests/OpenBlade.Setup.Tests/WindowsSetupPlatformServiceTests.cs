using System.Diagnostics;
using System.IO.Pipes;
using OpenBlade.Contracts;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

// This class captures process-wide Trace output; other collections must not emit into its log.
[Collection(nameof(SetupDiagnosticTraceCollection))]
public sealed class WindowsSetupPlatformServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateReadiness_BindsBothObservationsToTypedExchange(bool closeAfterResponse)
    {
        await using var fixture = new CandidateReadinessFixture();
        Task verification = fixture.VerifyAsync();
        Assert.Equal(fixture.Request, await fixture.ReadRequestAsync());
        Assert.Equal(1, fixture.ServiceReads);
        Assert.Equal(1, fixture.LaunchReads);

        await fixture.SendResponseAsync();
        if (closeAfterResponse)
        {
            fixture.Server.Close();
        }
        await verification;

        Assert.Equal(1, fixture.Connections);
        Assert.Equal(2, fixture.ServiceReads);
        Assert.Equal(2, fixture.LaunchReads);
        Assert.Empty(fixture.ServiceControl.Commands);
    }

    [Theory]
    [InlineData("pipe peer")]
    [InlineData("outgoing service PID")]
    [InlineData("stopped service")]
    [InlineData("service type")]
    [InlineData("start type")]
    [InlineData("service account")]
    [InlineData("service command")]
    [InlineData("process generation")]
    [InlineData("image path")]
    [InlineData("build digest")]
    [InlineData("lost retained handle")]
    public async Task CandidateReadiness_RejectsUnboundEvidenceBeforeWriting(string mismatch)
    {
        await using var fixture = new CandidateReadinessFixture(wrongPeer: mismatch == "pipe peer");
        switch (mismatch)
        {
            case "outgoing service PID": fixture.Service = fixture.Service with { ProcessId = fixture.Launch.ProcessId + 1 }; break;
            case "stopped service": fixture.Service = fixture.Service with { Running = false }; break;
            case "service type": fixture.Service = fixture.Service with { ServiceType = 0x20 }; break;
            case "start type": fixture.Service = fixture.Service with { StartType = 3 }; break;
            case "service account": fixture.Service = fixture.Service with { Account = "LocalService" }; break;
            case "service command": fixture.Service = fixture.Service with { CommandLine = fixture.Service.CommandLine + " --other" }; break;
            case "process generation": fixture.ObservedLaunch = fixture.Launch with { CreationFileTimeUtc = 2 }; break;
            case "image path": fixture.ObservedLaunch = fixture.Launch with { ExecutablePath = fixture.Launch.ExecutablePath + ".old" }; break;
            case "build digest": fixture.ObservedLaunch = fixture.Launch with { ExecutableSha256 = new string('b', 64) }; break;
            case "lost retained handle": fixture.ObservedLaunch = null; break;
        }

        Task verification = fixture.VerifyAsync();
        await fixture.AcceptAsync();
        await Assert.ThrowsAsync<IOException>(() => verification);

        Assert.Equal(0, await fixture.Server.ReadAsync(new byte[1], fixture.Deadline.Token));
        Assert.Equal(1, fixture.Connections);
        Assert.Empty(fixture.ServiceControl.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateReadiness_RejectsReplacementAfterAValidResponse(bool serviceChanged)
    {
        await using var fixture = new CandidateReadinessFixture();
        Task verification = fixture.VerifyAsync();
        Assert.Equal(fixture.Request, await fixture.ReadRequestAsync());

        if (serviceChanged)
        {
            fixture.Service = fixture.Service with { ProcessId = fixture.Launch.ProcessId + 1 };
        }
        else
        {
            fixture.ObservedLaunch = fixture.Launch with { CreationFileTimeUtc = 2 };
        }
        await fixture.SendResponseAsync();

        await Assert.ThrowsAsync<IOException>(() => verification);
        Assert.Equal(1, fixture.Connections);
        Assert.Equal(2, fixture.ServiceReads);
        Assert.Empty(fixture.ServiceControl.Commands);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("request ID")]
    [InlineData("protocol version")]
    [InlineData("missing status")]
    [InlineData("caller cancellation")]
    public async Task CandidateReadiness_RequiresCompleteValidResponse(string failure)
    {
        await using var fixture = new CandidateReadinessFixture();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(fixture.Deadline.Token);
        Task verification = fixture.VerifyAsync(caller.Token);
        Assert.Equal(fixture.Request, await fixture.ReadRequestAsync());

        ServiceResponse response = SetupServiceHealthProbeTests.CreateStatusResponse(fixture.Request);
        switch (failure)
        {
            case "disconnect":
                fixture.Server.Close();
                await Assert.ThrowsAnyAsync<IOException>(() => verification);
                break;
            case "request ID":
                await fixture.SendResponseAsync(response with { RequestId = Guid.NewGuid() });
                await Assert.ThrowsAsync<InvalidDataException>(() => verification);
                break;
            case "protocol version":
                await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(fixture.Server, response, fixture.Deadline.Token);
                await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(() => verification);
                break;
            case "missing status":
                await fixture.SendResponseAsync(response with { Status = null, Success = false, Error = "Status unavailable." });
                await Assert.ThrowsAsync<InvalidDataException>(() => verification);
                break;
            case "caller cancellation":
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
                break;
        }

        Assert.Equal(1, fixture.Connections);
        Assert.Equal(1, fixture.ServiceReads);
        Assert.Empty(fixture.ServiceControl.Commands);
    }

    [Fact]
    public async Task CandidateReadiness_BoundsAnUnansweredPipe()
    {
        await using var fixture = new CandidateReadinessFixture();
        Task verification = fixture.VerifyAsync();
        Assert.Equal(fixture.Request, await fixture.ReadRequestAsync());

        await Assert.ThrowsAsync<TimeoutException>(() => verification);
        Assert.Equal(1, fixture.Connections);
        Assert.Equal(1, fixture.ServiceReads);
        Assert.Empty(fixture.ServiceControl.Commands);
    }

    private sealed class CandidateReadinessFixture : ISetupRetainedCandidateLaunch, IAsyncDisposable
    {
        private readonly string pipeName = $"OpenBlade-candidate-test-{Guid.NewGuid():N}";
        internal readonly CancellationTokenSource Deadline = new(TimeSpan.FromSeconds(20));
        internal readonly NamedPipeServerStream Server;
        internal readonly GetStatusRequest Request = new(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        internal readonly RecordingServiceControl ServiceControl = new();
        internal readonly SetupCandidateLaunch Launch;
        internal SetupCandidateLaunch? ObservedLaunch;
        internal SetupCandidateServiceObservation Service;
        internal int Connections, ServiceReads, LaunchReads;
        private readonly Task accept;
        private readonly WindowsSetupPlatform platform;

        internal CandidateReadinessFixture(bool wrongPeer = false)
        {
            Server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            accept = Server.WaitForConnectionAsync(Deadline.Token);
            uint pid = (uint)Environment.ProcessId + (wrongPeer ? 1u : 0u);
            string image = Path.Combine(Path.GetTempPath(), "phase9d-test", "OpenBlade.Service.exe");
            Launch = new(pid, 1, image, new string('a', 64));
            ObservedLaunch = Launch;
            Service = new(pid, true, 0x10, 2, "LocalSystem", $"\"{image}\"");
            platform = WindowsSetupPlatformTestFactory.Create(ServiceControl);
        }

        internal Task VerifyAsync(CancellationToken cancellationToken = default)
            => platform.VerifyCandidateServiceHealthAsync(Launch, this, ReadServiceAsync,
                new SetupServiceHealthProbe(ConnectAsync, () => Request),
                cancellationToken == default ? Deadline.Token : cancellationToken);

        private async ValueTask<Stream> ConnectAsync(CancellationToken token)
        {
            Connections++;
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        private Task<SetupCandidateServiceObservation> ReadServiceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceReads++;
            return Task.FromResult(Service);
        }

        public ValueTask<SetupCandidateLaunch?> ReadLiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchReads++;
            return ValueTask.FromResult(ObservedLaunch);
        }

        internal Task AcceptAsync() => accept;

        internal async Task<ServiceRequest> ReadRequestAsync()
        {
            await accept;
            return await IpcMessageCodec.ReadRequestAsync(Server, Deadline.Token);
        }

        internal Task SendResponseAsync(ServiceResponse? response = null)
            => IpcMessageCodec.WriteServerFrameAsync(Server,
                response ?? SetupServiceHealthProbeTests.CreateStatusResponse(Request), Deadline.Token);

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Deadline.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceShutdownPaths_WaitForProcessExitBeforeCompletingOrDeleting(bool remove)
    {
        SetupPaths paths = UserPaths();
        ServiceExitProcess process = new(Path.Combine(paths.InstallDirectory, "OpenBlade.Service.exe"));
        ServiceExitControl control = new(SetupServiceState.Running);
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl: control, processOperations: new ServiceExitProcesses(process), paths: paths);
        Task operation = remove
            ? platform.RemoveServiceAsync(CancellationToken.None)
            : platform.RestoreServiceInstallStateAsync(new SetupServiceInstallState(true, false), CancellationToken.None);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        Assert.False(control.Deleted);
        process.Exit.SetResult();
        await operation;
        Assert.Equal(remove, control.Deleted);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void ServiceControlExecutable_IsPinnedToWindowsSystemDirectory()
    {
        Assert.Equal(
            Path.Combine(Environment.SystemDirectory, "sc.exe"),
            ScSetupServiceControl.ExecutablePath,
            ignoreCase: true);
    }

    [Fact]
    public async Task InstallService_NewService_RegistersLiteralQuotedImagePath()
    {
        RecordingServiceControl serviceControl = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        await platform.InstallServiceAsync(CancellationToken.None);

        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade",
            "OpenBlade.Service.exe");
        RecordedCommand create = Assert.Single(
            serviceControl.Commands,
            command => command.Arguments[0] == "create");
        Assert.Contains($"\"{executable}\"", create.Arguments);
        Assert.Contains("OpenBlade Service", create.Arguments);
        Assert.All(serviceControl.Commands, command => Assert.Equal("OpenBlade", command.Arguments[1]));
    }

    [Fact]
    public async Task InstallService_SetsExactDescription()
    {
        RecordingServiceControl serviceControl = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        await platform.InstallServiceAsync(CancellationToken.None);

        RecordedCommand description = Assert.Single(
            serviceControl.Commands,
            command => command.Arguments[0] == "description");
        Assert.Equal(
            ["description", "OpenBlade", "OpenBlade hardware control and profile service"],
            description.Arguments);
    }

    [Fact]
    public async Task InstallService_ExistingService_UpdatesConfigurationInsteadOfCreating()
    {
        RecordingServiceControl serviceControl = new() { Exists = true };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        await platform.InstallServiceAsync(CancellationToken.None);

        Assert.DoesNotContain(serviceControl.Commands, command => command.Arguments[0] == "create");
        Assert.Contains(serviceControl.Commands, command => command.Arguments[0] == "config");
    }

    [Fact]
    public async Task InstallService_ConfiguresBoundedRestartRecovery()
    {
        RecordingServiceControl serviceControl = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        await platform.InstallServiceAsync(CancellationToken.None);

        RecordedCommand recovery = Assert.Single(
            serviceControl.Commands,
            command => command.Arguments[0] == "failure");
        Assert.Equal(
            ["failure", "OpenBlade", "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"],
            recovery.Arguments);
        Assert.Contains(
            serviceControl.Commands,
            command => command.Arguments.SequenceEqual(["failureflag", "OpenBlade", "1"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallService_ProtectsMachineDataBeforeRegistrationOrStart(bool existingService)
    {
        RecordingServiceControl serviceControl = new() { Exists = existingService };
        RecordingMachineDataDirectorySecurity machineDataSecurity = new()
        {
            OnProtect = () => Assert.Empty(serviceControl.Commands),
        };
        RecordingAuthorizedSidWriter sidWriter = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl, sidWriter: sidWriter, machineDataDirectorySecurity: machineDataSecurity);

        await platform.InstallServiceAsync(CancellationToken.None);

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade"),
            Assert.Single(machineDataSecurity.ProtectedDirectories),
            ignoreCase: true);
        Assert.False(Assert.Single(machineDataSecurity.IncludeDescendants));
        Assert.Empty(sidWriter.Requests);
        Assert.Contains(serviceControl.Commands, command => command.Arguments[0] == "start");
    }

    [Fact]
    public async Task InstallService_MachineDataSecurityFailure_DoesNotConfigureOrStartService()
    {
        RecordingServiceControl serviceControl = new() { Exists = true };
        RecordingMachineDataDirectorySecurity machineDataSecurity = new()
        {
            Failure = new UnauthorizedAccessException("ACL write denied."),
        };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl, machineDataDirectorySecurity: machineDataSecurity);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => platform.InstallServiceAsync(CancellationToken.None));

        Assert.Empty(serviceControl.Commands);
    }

    [Fact]
    public async Task InstallService_StartsThenWaitsForRunning()
    {
        RecordingServiceControl serviceControl = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        await platform.InstallServiceAsync(CancellationToken.None);

        Assert.Equal(
            ["run:start", "wait:Running:00:00:30"],
            serviceControl.Operations.Where(operation =>
                operation == "run:start" || operation.StartsWith("wait:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CaptureThenPrepareService_RunningService_RecordsStateBeforeStopping()
    {
        RecordingServiceControl serviceControl = new() { State = SetupServiceState.Running };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        SetupInstallLifecycleState prior = await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);
        await platform.PrepareServiceForInstallAsync(CancellationToken.None);

        Assert.Equal(new SetupServiceInstallState(true, true), prior.Service);
        Assert.Equal(["run:stop", "wait:Stopped:00:00:30"], serviceControl.Operations);
    }

    [Fact]
    public async Task CaptureThenPrepareService_StoppedService_RecordsStateWithoutStartingOrStoppingIt()
    {
        RecordingServiceControl serviceControl = new() { State = SetupServiceState.Stopped };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        SetupInstallLifecycleState prior = await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);
        await platform.PrepareServiceForInstallAsync(CancellationToken.None);

        Assert.Equal(new SetupServiceInstallState(true, false), prior.Service);
        Assert.Empty(serviceControl.Operations);
    }

    [Fact]
    public async Task CaptureInstallLifecycle_ReadsShortcutExistenceWithoutMutatingIt()
    {
        RecordingFileOperations files = new() { FileExistsResult = true };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(fileOperations: files);

        SetupInstallLifecycleState prior =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.Equal(SetupStartupRegistration.Shortcut, prior.Startup.Tray);
        Assert.Equal(SetupStartupRegistration.Shortcut, prior.Startup.SessionAgent);
        Assert.Empty(files.DeletedFiles);
    }

    [Fact]
    public async Task RestoreServiceInstallState_PriorRunningAndStillRunning_DoesNotStartAgainAndChecksHealth()
    {
        RecordingServiceControl serviceControl = new() { State = SetupServiceState.Running };
        RecordingHealthProbe health = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl,
            healthProbe: health);

        await platform.RestoreServiceInstallStateAsync(new(true, true), CancellationToken.None);

        Assert.DoesNotContain(serviceControl.Commands, command => command.Arguments[0] == "start");
        Assert.Equal(1, health.Verifications);
    }

    [Fact]
    public async Task HealthFailures_RetainDistinctInstallAndRollbackTimings()
    {
        string logPath = Path.GetTempFileName();
        using var listener = new SetupDiagnosticTraceListener(new SetupLog(logPath));
        Trace.Listeners.Add(listener);
        try
        {
            var serviceControl = new RecordingServiceControl { State = SetupServiceState.Running };
            int attempts = 0;
            Exception[] failures =
            [
                new TaskCanceledException("Private failure message"),
                new IOException("Private failure message"),
            ];
            var probe = new SetupServiceHealthProbe(
                _ => ValueTask.FromException<Stream>(failures[attempts++]),
                () => new(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()));
            WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl, healthProbe: probe);

            TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(
                () => platform.VerifyServiceHealthAsync(CancellationToken.None));
            Assert.IsType<TaskCanceledException>(timeout.InnerException);
            await Assert.ThrowsAsync<IOException>(() => platform.RestoreServiceInstallStateAsync(
                new(true, true), CancellationToken.None));
            var customProbe = new RecordingHealthProbe();
            await WindowsSetupPlatformTestFactory.Create(serviceControl, healthProbe: customProbe)
                .VerifyServiceHealthAsync(CancellationToken.None);

            string[] entries = (await File.ReadAllLinesAsync(logPath))
                .Where(line => line.Contains("OpenBlade setup service health timing.", StringComparison.Ordinal))
                .ToArray();
            Assert.Collection(entries,
                install =>
                {
                    Assert.Contains("Purpose=install", install);
                    Assert.Contains("Outcome=TimeoutException Cause=TaskCanceledException", install);
                },
                rollback =>
                {
                    Assert.Contains("Purpose=rollback", rollback);
                    Assert.Contains("Outcome=IOException", rollback);
                },
                custom => Assert.Contains("StageTiming=unavailable", custom));
            Assert.All(entries.Take(2), entry =>
            {
                Assert.Contains("BudgetMs=30000", entry);
                Assert.Contains("Connect=[", entry);
                Assert.Contains("Succeeded=False", entry);
                Assert.Contains("HeaderWrite=not-entered", entry);
                Assert.DoesNotContain("Private failure message", entry);
            });
            Assert.Equal(1, customProbe.Verifications);
            Assert.Empty(serviceControl.Commands);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task RestoreServiceInstallState_PriorStoppedAndRegistrationMissing_RecreatesWithoutStarting()
    {
        RecordingServiceControl serviceControl = new();
        RecordingHealthProbe health = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl,
            healthProbe: health);

        await platform.RestoreServiceInstallStateAsync(new(true, false), CancellationToken.None);

        Assert.Contains(serviceControl.Commands, command => command.Arguments[0] == "create");
        Assert.DoesNotContain(serviceControl.Commands, command => command.Arguments[0] == "start");
        Assert.Equal(0, health.Verifications);
    }

    [Fact]
    public async Task StopTray_FreshInstallWithNoTrayProcesses_IsSafe()
    {
        RecordingProcessOperations processes = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(processOperations: processes);

        await platform.StopTrayAsync(CancellationToken.None);

        Assert.Equal(["query:OpenBlade.Tray"], processes.Operations);
    }

    [Fact]
    public async Task StopTray_InstalledTray_SignalsAndWaitsForExit()
    {
        string installedTray = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade",
            "OpenBlade.Tray.exe");
        RecordingProcess process = new(installedTray);
        RecordingProcessOperations processes = new(process);
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(processOperations: processes);

        await platform.StopTrayAsync(CancellationToken.None);

        Assert.Collection(
            processes.Operations,
            operation => Assert.Equal("query:OpenBlade.Tray", operation),
            operation => Assert.StartsWith("signal:Local\\OpenBlade.Tray.Shutdown.", operation, StringComparison.Ordinal));
        Assert.True(process.WaitedForExit);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task StopSessionAgent_InstalledAgent_SignalsDistinctPathBoundEndpointAndWaits()
    {
        string installedAgent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade",
            "OpenBlade.SessionAgent.exe");
        RecordingProcess process = new(installedAgent);
        RecordingProcessOperations processes = new(process);
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(processOperations: processes);

        await platform.StopSessionAgentAsync(CancellationToken.None);

        Assert.Collection(
            processes.Operations,
            operation => Assert.Equal("query:OpenBlade.SessionAgent", operation),
            operation => Assert.StartsWith(
                "signal:Local\\OpenBlade.SessionAgent.Shutdown.",
                operation,
                StringComparison.Ordinal));
        Assert.True(process.WaitedForExit);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task StopSessionAgent_SameNamedForeignProcess_IsDisposedWithoutSignalOrWait()
    {
        RecordingProcess foreign = new(@"C:\worktree\OpenBlade.SessionAgent.exe");
        RecordingProcessOperations processes = new(foreign);
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(processOperations: processes);

        await platform.StopSessionAgentAsync(CancellationToken.None);

        Assert.Equal(["query:OpenBlade.SessionAgent"], processes.Operations);
        Assert.False(foreign.WaitedForExit);
        Assert.True(foreign.Disposed);
    }

    [Fact]
    public async Task StopSessionAgent_MissingGracefulEndpoint_FailsWithoutForceTermination()
    {
        string installedAgent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade",
            "OpenBlade.SessionAgent.exe");
        RecordingProcess process = new(installedAgent);
        RecordingProcessOperations processes = new(process) { SignalResult = false };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(processOperations: processes);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => platform.StopSessionAgentAsync(CancellationToken.None));

        Assert.Contains("does not expose its shutdown event", failure.Message, StringComparison.Ordinal);
        Assert.False(process.WaitedForExit);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RemoveService_StopsWaitsDeletesThenWaitsForDeletion()
    {
        RecordingServiceControl serviceControl = new() { Exists = true };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        await platform.RemoveServiceAsync(CancellationToken.None);

        Assert.Equal(
            ["run:stop", "wait:Stopped:00:00:30", "run:delete", "wait-deleted:00:00:30"],
            serviceControl.Operations);
    }

    private static SetupPaths UserPaths() => SetupPaths.Create(
        @"C:\Program Files",
        @"C:\ProgramData",
        @"C:\Users\jack\AppData\Local",
        @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
        @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup");

    private static SetupPaths SystemPaths() => SetupPaths.CreateLocalSystem(
        @"C:\Program Files",
        @"C:\ProgramData",
        @"C:\Windows\System32\config\systemprofile\AppData\Local",
        @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup");

    [Fact]
    public async Task InstallTrayStartup_RegistersExactSignInTask()
    {
        RecordingSignInTaskScheduler scheduler = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: new RecordingFileOperations(),
            paths: UserPaths(),
            signInTaskScheduler: scheduler);

        await platform.InstallTrayStartupAsync(CancellationToken.None);

        SignInTaskDefinition definition = Assert.Single(scheduler.Registered);
        Assert.Equal(
            new SignInTaskDefinition(
                "OpenBlade Tray",
                @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
                @"C:\Program Files\OpenBlade",
                StartupShortcutArguments.Tray,
                "Starts OpenBlade when you sign in."),
            definition);
    }

    [Fact]
    public async Task InstallTrayStartup_RemovesCommonLegacyPerUserAndCmdEntries()
    {
        RecordingFileOperations files = new();
        SetupPaths paths = UserPaths();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            paths: paths);

        await platform.InstallTrayStartupAsync(CancellationToken.None);

        Assert.Equal(
            [paths.StartupFile, paths.LegacyTrayStartupFile, paths.LegacyStartupFile],
            files.DeletedFiles);
    }

    [Fact]
    public async Task InstallTrayStartup_LocalSystemPathsDeleteEachFileOnce()
    {
        RecordingFileOperations files = new();
        SetupPaths paths = SystemPaths();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            paths: paths);

        await platform.InstallTrayStartupAsync(CancellationToken.None);

        Assert.Equal([paths.StartupFile, paths.LegacyStartupFile], files.DeletedFiles);
    }

    [Fact]
    public async Task InstallTrayStartup_RegisterFailureKeepsExistingFiles()
    {
        RecordingFileOperations files = new();
        RecordingSignInTaskScheduler scheduler = new()
        {
            RegisterFailure = new InvalidOperationException("register failed"),
        };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            signInTaskScheduler: scheduler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            platform.InstallTrayStartupAsync(CancellationToken.None));

        Assert.Empty(files.DeletedFiles);
    }

    [Fact]
    public async Task InstallSessionAgentStartup_RegistersExactSignInTaskWithoutArguments()
    {
        RecordingSignInTaskScheduler scheduler = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: new RecordingFileOperations(),
            paths: UserPaths(),
            signInTaskScheduler: scheduler);

        await platform.InstallSessionAgentStartupAsync(CancellationToken.None);

        SignInTaskDefinition definition = Assert.Single(scheduler.Registered);
        Assert.Equal(
            new SignInTaskDefinition(
                "OpenBlade Session Agent",
                @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe",
                @"C:\Program Files\OpenBlade",
                null,
                "Starts the OpenBlade Session Agent when you sign in."),
            definition);
    }

    [Fact]
    public async Task InstallSessionAgentStartup_RemovesCommonAndLegacyLinks()
    {
        RecordingFileOperations files = new();
        SetupPaths paths = UserPaths();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            paths: paths);

        await platform.InstallSessionAgentStartupAsync(CancellationToken.None);

        Assert.Equal(
            [paths.SessionAgentStartupFile, paths.LegacySessionAgentStartupFile],
            files.DeletedFiles);
    }

    [Fact]
    public async Task InstallSessionAgentStartup_RegisterFailureKeepsExistingFiles()
    {
        RecordingFileOperations files = new();
        RecordingSignInTaskScheduler scheduler = new()
        {
            RegisterFailure = new InvalidOperationException("register failed"),
        };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            signInTaskScheduler: scheduler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            platform.InstallSessionAgentStartupAsync(CancellationToken.None));

        Assert.Empty(files.DeletedFiles);
    }

    [Fact]
    public async Task RemoveTrayStartup_DeletesTaskAndAllStartupFiles()
    {
        RecordingFileOperations files = new();
        RecordingSignInTaskScheduler scheduler = new();
        SetupPaths paths = UserPaths();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            paths: paths,
            signInTaskScheduler: scheduler);

        await platform.RemoveTrayStartupAsync(CancellationToken.None);

        Assert.Equal(["OpenBlade Tray"], scheduler.Deleted);
        Assert.Equal(
            [paths.StartupFile, paths.LegacyTrayStartupFile, paths.LegacyStartupFile],
            files.DeletedFiles);
    }

    [Fact]
    public async Task RemoveSessionAgentStartup_DeletesOnlyAgentTaskAndFiles()
    {
        RecordingFileOperations files = new();
        RecordingSignInTaskScheduler scheduler = new();
        SetupPaths paths = UserPaths();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            paths: paths,
            signInTaskScheduler: scheduler);

        await platform.RemoveSessionAgentStartupAsync(CancellationToken.None);

        Assert.Equal(["OpenBlade Session Agent"], scheduler.Deleted);
        Assert.Equal(
            [paths.SessionAgentStartupFile, paths.LegacySessionAgentStartupFile],
            files.DeletedFiles);
    }

    [Fact]
    public async Task CaptureInstallLifecycle_NothingPresent_ReportsNone()
    {
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(paths: UserPaths());

        SetupInstallLifecycleState prior =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.Equal(SetupStartupRegistration.None, prior.Startup.Tray);
        Assert.Equal(SetupStartupRegistration.None, prior.Startup.SessionAgent);
    }

    [Theory]
    [InlineData(SignInTaskState.Enabled)]
    [InlineData(SignInTaskState.Disabled)]
    public async Task CaptureInstallLifecycle_TaskPresent_BeatsShortcutPerComponent(
        SignInTaskState state)
    {
        RecordingFileOperations files = new() { FileExistsResult = true };
        RecordingSignInTaskScheduler scheduler = new();
        scheduler.States["OpenBlade Tray"] = state;
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            paths: UserPaths(),
            signInTaskScheduler: scheduler);

        SetupInstallLifecycleState prior =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.Equal(SetupStartupRegistration.SignInTask, prior.Startup.Tray);
        Assert.Equal(SetupStartupRegistration.Shortcut, prior.Startup.SessionAgent);
        Assert.Empty(files.DeletedFiles);
        Assert.Empty(scheduler.Registered);
    }

    [Fact]
    public async Task CaptureInstallLifecycle_SessionAgentTaskOnly_DoesNotAffectTray()
    {
        RecordingSignInTaskScheduler scheduler = new();
        scheduler.States["OpenBlade Session Agent"] = SignInTaskState.Enabled;
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: new RecordingFileOperations(),
            paths: UserPaths(),
            signInTaskScheduler: scheduler);

        SetupInstallLifecycleState prior =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.Equal(SetupStartupRegistration.None, prior.Startup.Tray);
        Assert.Equal(SetupStartupRegistration.SignInTask, prior.Startup.SessionAgent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureInstallLifecycle_LegacyShortcutOnly_ReportsShortcut(bool localSystem)
    {
        SetupPaths paths = localSystem ? SystemPaths() : UserPaths();
        RecordingFileOperations trayFiles = new();
        trayFiles.ExistingFiles.Add(paths.LegacyTrayStartupFile);
        WindowsSetupPlatform trayPlatform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: trayFiles,
            paths: paths);
        RecordingFileOperations agentFiles = new();
        agentFiles.ExistingFiles.Add(paths.LegacySessionAgentStartupFile);
        WindowsSetupPlatform agentPlatform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: agentFiles,
            paths: paths);

        SetupInstallLifecycleState tray =
            await trayPlatform.CaptureInstallLifecycleStateAsync(CancellationToken.None);
        SetupInstallLifecycleState agent =
            await agentPlatform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.Equal(SetupStartupRegistration.Shortcut, tray.Startup.Tray);
        Assert.Equal(SetupStartupRegistration.None, tray.Startup.SessionAgent);
        Assert.Equal(SetupStartupRegistration.None, agent.Startup.Tray);
        Assert.Equal(SetupStartupRegistration.Shortcut, agent.Startup.SessionAgent);
    }

    [Fact]
    public async Task CaptureInstallLifecycle_LegacyCmdOnly_ReportsTrayShortcut()
    {
        SetupPaths paths = UserPaths();
        RecordingFileOperations files = new();
        files.ExistingFiles.Add(paths.LegacyStartupFile);
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            paths: paths);

        SetupInstallLifecycleState prior =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.Equal(SetupStartupRegistration.Shortcut, prior.Startup.Tray);
        Assert.Equal(SetupStartupRegistration.None, prior.Startup.SessionAgent);
    }

    [Fact]
    public async Task RestoreTrayStartup_Shortcut_WritesOnlyCommonLinkAndNoTask()
    {
        RecordingShortcutWriter shortcutWriter = new();
        RecordingSignInTaskScheduler scheduler = new();
        SetupPaths paths = UserPaths();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            shortcutWriter: shortcutWriter,
            paths: paths,
            signInTaskScheduler: scheduler);

        await platform.RestoreTrayStartupAsync(
            SetupStartupRegistration.Shortcut,
            CancellationToken.None);

        ShortcutRequest request = Assert.Single(shortcutWriter.Requests);
        Assert.Equal(paths.StartupFile, request.ShortcutPath);
        Assert.Equal(@"C:\Program Files\OpenBlade\OpenBlade.Tray.exe", request.TargetPath);
        Assert.Equal(@"C:\Program Files\OpenBlade", request.WorkingDirectory);
        Assert.Equal(StartupShortcutArguments.Tray, request.Arguments);
        Assert.Empty(scheduler.Registered);
    }

    [Fact]
    public async Task RestoreSessionAgentStartup_Shortcut_WritesOnlyCommonLinkAndNoTask()
    {
        RecordingShortcutWriter shortcutWriter = new();
        RecordingSignInTaskScheduler scheduler = new();
        SetupPaths paths = UserPaths();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            shortcutWriter: shortcutWriter,
            paths: paths,
            signInTaskScheduler: scheduler);

        await platform.RestoreSessionAgentStartupAsync(
            SetupStartupRegistration.Shortcut,
            CancellationToken.None);

        ShortcutRequest request = Assert.Single(shortcutWriter.Requests);
        Assert.Equal(paths.SessionAgentStartupFile, request.ShortcutPath);
        Assert.Equal(
            @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe",
            request.TargetPath);
        Assert.Equal(@"C:\Program Files\OpenBlade", request.WorkingDirectory);
        Assert.Null(request.Arguments);
        Assert.Empty(scheduler.Registered);
    }

    [Fact]
    public async Task RestoreStartup_SignInTask_RegistersBothTasksWithoutShortcuts()
    {
        RecordingShortcutWriter shortcutWriter = new();
        RecordingSignInTaskScheduler scheduler = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            shortcutWriter: shortcutWriter,
            fileOperations: new RecordingFileOperations(),
            paths: UserPaths(),
            signInTaskScheduler: scheduler);

        await platform.RestoreTrayStartupAsync(
            SetupStartupRegistration.SignInTask,
            CancellationToken.None);
        await platform.RestoreSessionAgentStartupAsync(
            SetupStartupRegistration.SignInTask,
            CancellationToken.None);

        Assert.Equal(
            ["OpenBlade Tray", "OpenBlade Session Agent"],
            scheduler.Registered.Select(task => task.TaskName));
        Assert.Empty(shortcutWriter.Requests);
    }

    [Fact]
    public async Task RestoreStartup_None_DoesNothing()
    {
        RecordingShortcutWriter shortcutWriter = new();
        RecordingSignInTaskScheduler scheduler = new();
        RecordingFileOperations files = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            shortcutWriter: shortcutWriter,
            fileOperations: files,
            signInTaskScheduler: scheduler);

        await platform.RestoreTrayStartupAsync(SetupStartupRegistration.None, CancellationToken.None);
        await platform.RestoreSessionAgentStartupAsync(
            SetupStartupRegistration.None,
            CancellationToken.None);

        Assert.Empty(shortcutWriter.Requests);
        Assert.Empty(scheduler.Registered);
        Assert.Empty(scheduler.Deleted);
        Assert.Empty(files.DeletedFiles);
    }

    [Fact]
    public async Task InstallStartMenuShortcut_CreatesSearchableLinkToTray()
    {
        RecordingShortcutWriter shortcutWriter = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            shortcutWriter);

        await platform.InstallStartMenuShortcutAsync(CancellationToken.None);

        string installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade");
        ShortcutRequest request = Assert.Single(shortcutWriter.Requests);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "OpenBlade.lnk"),
            request.ShortcutPath);
        Assert.Equal(Path.Combine(installDirectory, "OpenBlade.Tray.exe"), request.TargetPath);
        Assert.Equal(installDirectory, request.WorkingDirectory);
    }

    [Fact]
    public async Task RemoveStartMenuShortcut_RemovesSearchableEntry()
    {
        RecordingFileOperations files = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(fileOperations: files);

        await platform.RemoveStartMenuShortcutAsync(CancellationToken.None);

        string removed = Assert.Single(files.DeletedFiles);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "OpenBlade.lnk"),
            removed);
    }

    [Fact]
    public async Task InstallUninstallShortcut_TargetsInstalledSetupAndPreservesProfilesByDefault()
    {
        RecordingShortcutWriter shortcutWriter = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            shortcutWriter);

        await platform.InstallUninstallShortcutAsync(CancellationToken.None);

        ShortcutRequest request = Assert.Single(shortcutWriter.Requests);
        Assert.EndsWith("Uninstall OpenBlade.lnk", request.ShortcutPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("OpenBlade.Setup.exe", request.TargetPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("uninstall --interactive", request.Arguments);
        Assert.Contains("keep saved profiles", request.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, request.WindowStyle);
    }

    [Fact]
    public async Task InstallUninstallRegistration_CreatesInstalledAppsEntryForInstalledSetup()
    {
        RecordingUninstallRegistration registration = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            uninstallRegistration: registration);

        await platform.InstallUninstallRegistrationAsync(CancellationToken.None);

        UninstallRegistrationRequest request = Assert.Single(registration.Installs);
        Assert.EndsWith(@"\OpenBlade", request.InstallLocation, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\OpenBlade.Tray.exe", request.DisplayIcon, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\OpenBlade.Setup.exe\" uninstall --interactive", request.UninstallCommand, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(request.DisplayVersion));
    }

    [Fact]
    public async Task InstallUninstaller_CopiesRunningSetupIntoActivatedInstallDirectory()
    {
        RecordingInstalledUninstallerWriter writer = new();
        const string source = @"C:\Downloads\OpenBlade.Setup.exe";
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            installedUninstallerWriter: writer,
            setupExecutablePath: source);

        await platform.InstallUninstallerAsync(CancellationToken.None);

        InstalledUninstallerWriteRequest request = Assert.Single(writer.Requests);
        Assert.Equal(source, request.SourceExecutable);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "OpenBlade",
                "OpenBlade.Setup.exe"),
            request.DestinationExecutable);
    }

    [Fact]
    public async Task RemoveUninstallArtifacts_RemovesShortcutAndInstalledAppsEntry()
    {
        RecordingFileOperations files = new();
        RecordingUninstallRegistration registration = new() { EntryExists = true };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            uninstallRegistration: registration);

        await platform.RemoveUninstallShortcutAsync(CancellationToken.None);
        await platform.RemoveUninstallRegistrationAsync(CancellationToken.None);

        Assert.EndsWith(
            "Uninstall OpenBlade.lnk",
            Assert.Single(files.DeletedFiles),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, registration.Removals);
    }

    [Fact]
    public async Task CaptureInstallLifecycle_RecordsUninstallArtifactExistence()
    {
        RecordingFileOperations files = new() { FileExistsResult = true };
        RecordingUninstallRegistration registration = new() { EntryExists = true };
        RecordingBundleOwnershipRegistration bundleOwnership = new()
        {
            EntryExists = true,
            OwnedByCurrentBuild = false,
        };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            fileOperations: files,
            uninstallRegistration: registration,
            bundleOwnershipRegistration: bundleOwnership);

        SetupInstallLifecycleState state =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.True(state.Startup.UninstallShortcutExisted);
        Assert.True(state.Startup.UninstallRegistrationExisted);
        Assert.True(state.BundleOwnershipExisted);
        Assert.False(state.BundleOwnershipMatchesCurrentBuild);
        Assert.Equal(bundleOwnership.Ownership, state.BundleOwnership);
    }

    [Fact]
    public async Task BundleOwnershipOperations_ForwardToRegistration()
    {
        RecordingBundleOwnershipRegistration bundleOwnership = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            bundleOwnershipRegistration: bundleOwnership);

        await platform.InstallBundleOwnershipAsync(CancellationToken.None);
        await platform.RestoreBundleOwnershipAsync(
            bundleOwnership.Ownership,
            CancellationToken.None);
        await platform.RemoveBundleOwnershipAsync(CancellationToken.None);

        Assert.Equal(1, bundleOwnership.Installs);
        Assert.Equal([bundleOwnership.Ownership], bundleOwnership.Restores);
        Assert.Equal(1, bundleOwnership.Removals);
    }

    [Fact]
    public async Task DynamicLightingIdentityOperations_UseInstallDirectoryAndInstallingUser()
    {
        const string enrolledUserSid = "S-1-5-21-1-2-3-1001";
        RecordingDynamicLightingIdentityRegistration identity = new()
        {
            Registered = true,
        };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            dynamicLightingIdentityRegistration: identity,
            currentUserSid: enrolledUserSid);

        SetupInstallLifecycleState state =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);
        await platform.RemoveDynamicLightingIdentityAsync(CancellationToken.None);
        await platform.RestoreDynamicLightingIdentityAsync(CancellationToken.None);

        Assert.True(state.DynamicLightingIdentityRegistered);
        DynamicLightingIdentityRequest query = Assert.Single(identity.Queries);
        DynamicLightingIdentityRequest removal = Assert.Single(identity.Removals);
        DynamicLightingIdentityRequest install = Assert.Single(identity.Installs);
        Assert.Equal(query, removal);
        Assert.Equal(query, install);
        Assert.True(Path.IsPathFullyQualified(query.InstallDirectory));
        Assert.EndsWith(
            Path.Combine("Program Files", "OpenBlade"),
            query.InstallDirectory,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(enrolledUserSid, platform.CurrentUserSid);
        Assert.Equal(enrolledUserSid, query.UserSid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-1-0")]
    [InlineData("s-1-5-21-1-2-3-1001")]
    public void EnrolledUserOverride_RejectsNoncanonicalAccountSid(string sid) =>
        Assert.Throws<InvalidDataException>(
            () => WindowsSetupPlatformTestFactory.Create(currentUserSid: sid));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-1-0")]
    public async Task WriteAuthorizedUserSid_InvalidOrNonAccountSid_FailsClosed(string sid)
    {
        RecordingAuthorizedSidWriter sidWriter = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            sidWriter);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => platform.WriteAuthorizedUserSidAsync(sid, CancellationToken.None));

        Assert.Contains("valid Windows account SID", exception.Message, StringComparison.Ordinal);
        Assert.Empty(sidWriter.Requests);
    }

    [Fact]
    public async Task WriteAuthorizedUserSid_ValidSid_UsesCanonicalValueAndMachineDataPath()
    {
        RecordingAuthorizedSidWriter sidWriter = new();
        RecordingMachineDataDirectorySecurity machineDataSecurity = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            sidWriter,
            machineDataDirectorySecurity: machineDataSecurity);

        await platform.WriteAuthorizedUserSidAsync(
            " S-1-5-21-1-2-3-1001 ",
            CancellationToken.None);

        AuthorizedSidWriteRequest request = Assert.Single(sidWriter.Requests);
        Assert.Equal("S-1-5-21-1-2-3-1001", request.Sid);
        Assert.EndsWith(
            Path.Combine("OpenBlade", "authorized-user.sid"),
            request.Path,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetDirectoryName(request.Path),
            Assert.Single(machineDataSecurity.ProtectedDirectories));
    }

    [Fact]
    public async Task WriteAuthorizedUserSid_MachineDataSecurityFailure_DoesNotWriteSid()
    {
        RecordingAuthorizedSidWriter sidWriter = new();
        RecordingMachineDataDirectorySecurity machineDataSecurity = new()
        {
            Failure = new UnauthorizedAccessException("ACL write denied."),
        };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            sidWriter,
            machineDataDirectorySecurity: machineDataSecurity);

        UnauthorizedAccessException failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => platform.WriteAuthorizedUserSidAsync(
                "S-1-5-21-1-2-3-1001",
                CancellationToken.None));

        Assert.Equal("ACL write denied.", failure.Message);
        Assert.Single(machineDataSecurity.ProtectedDirectories);
        Assert.Empty(sidWriter.Requests);
    }

    [Fact]
    public async Task InstallAndRemoveService_NeverTargetsFormerServiceIdentity()
    {
        RecordingServiceControl serviceControl = new() { Exists = true };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(serviceControl);

        await platform.PrepareServiceForInstallAsync(CancellationToken.None);
        await platform.InstallServiceAsync(CancellationToken.None);
        await platform.RemoveServiceAsync(CancellationToken.None);

        Assert.DoesNotContain(
            serviceControl.Commands.SelectMany(command => command.Arguments),
            argument => argument.Contains("Open" + "Synapse", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record RecordedCommand(string[] Arguments, int[] ToleratedExitCodes);

    private sealed class RecordingServiceControl : ISetupServiceControl
    {
        public bool Exists { get; init; }
        public SetupServiceState? State { get; init; }
        public List<RecordedCommand> Commands { get; } = [];
        public List<string> Operations { get; } = [];

        public Task<bool> ExistsAsync(string serviceName, CancellationToken cancellationToken) =>
            Task.FromResult(Exists || State is not null);

        public Task<SetupServiceState?> GetStateAsync(string serviceName, CancellationToken cancellationToken) =>
            Task.FromResult(State ?? (Exists ? SetupServiceState.Running : null));

        public async Task<SetupServiceStatus?> GetStatusAsync(string serviceName, CancellationToken cancellationToken)
        {
            SetupServiceState? state = await GetStateAsync(serviceName, cancellationToken);
            return state is null ? null : new(state.Value, state == SetupServiceState.Running ? 123 : null);
        }

        public Task RunAsync(
            IReadOnlyList<string> arguments,
            IReadOnlyCollection<int> toleratedExitCodes,
            CancellationToken cancellationToken)
        {
            Commands.Add(new(arguments.ToArray(), toleratedExitCodes.ToArray()));
            Operations.Add($"run:{arguments[0]}");
            return Task.CompletedTask;
        }

        public Task WaitForStateAsync(
            string serviceName,
            SetupServiceState state,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Operations.Add($"wait:{state}:{timeout}");
            return Task.CompletedTask;
        }

        public Task WaitForDeletionAsync(
            string serviceName,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Operations.Add($"wait-deleted:{timeout}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSignInTaskScheduler : ISignInTaskScheduler
    {
        public Dictionary<string, SignInTaskState> States { get; } = [];
        public List<SignInTaskDefinition> Registered { get; } = [];
        public List<string> Deleted { get; } = [];
        public Exception? RegisterFailure { get; init; }

        public SignInTaskState GetState(string taskName) =>
            States.TryGetValue(taskName, out SignInTaskState state)
                ? state
                : SignInTaskState.Missing;

        public void Register(SignInTaskDefinition definition)
        {
            if (RegisterFailure is not null)
            {
                throw RegisterFailure;
            }

            Registered.Add(definition);
            States[definition.TaskName] = SignInTaskState.Enabled;
        }

        public void Delete(string taskName)
        {
            Deleted.Add(taskName);
            States.Remove(taskName);
        }
    }

    private sealed class RecordingShortcutWriter : IStartupShortcutWriter
    {
        public List<ShortcutRequest> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public Task CreateAsync(ShortcutRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Failure is null
                ? Task.CompletedTask
                : Task.FromException(Failure);
        }
    }

    private sealed class RecordingAuthorizedSidWriter : IAuthorizedUserSidWriter
    {
        public List<AuthorizedSidWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AuthorizedSidWriteRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMachineDataDirectorySecurity
        : IMachineDataDirectorySecurity
    {
        public List<string> ProtectedDirectories { get; } = [];
        public List<bool> IncludeDescendants { get; } = [];
        public Exception? Failure { get; init; }
        public Action? OnProtect { get; init; }

        public Task ProtectAsync(string directory, CancellationToken cancellationToken, bool includeDescendants = true)
        {
            OnProtect?.Invoke();
            ProtectedDirectories.Add(directory);
            IncludeDescendants.Add(includeDescendants);
            return Failure is null
                ? Task.CompletedTask
                : Task.FromException(Failure);
        }
    }

    private sealed class RecordingFileOperations : ISetupFileOperations
    {
        public List<string> DeletedFiles { get; } = [];
        public bool FileExistsResult { get; init; }
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => FileExistsResult || ExistingFiles.Contains(path);

        public void DeleteFile(string path) => DeletedFiles.Add(path);
    }

    private sealed class RecordingUninstallRegistration : IUninstallRegistration
    {
        public bool EntryExists { get; init; }
        public List<UninstallRegistrationRequest> Installs { get; } = [];
        public int Removals { get; private set; }

        public bool Exists() => EntryExists;
        public void Install(UninstallRegistrationRequest request) => Installs.Add(request);
        public void Remove() => Removals++;
    }

    private sealed class RecordingBundleOwnershipRegistration : IBundleOwnershipRegistration
    {
        public bool EntryExists { get; init; }
        public bool OwnedByCurrentBuild { get; init; } = true;
        public BundleOwnershipIdentity Ownership { get; init; } = new(
            "WiX Burn",
            "0.1.0",
            "0.1.0+captured");
        public int Installs { get; private set; }
        public List<BundleOwnershipIdentity> Restores { get; } = [];
        public int Removals { get; private set; }

        public BundleOwnershipIdentity? Capture() =>
            EntryExists ? Ownership : null;
        public bool IsOwnedByCurrentBuild(BundleOwnershipIdentity ownership) =>
            OwnedByCurrentBuild;
        public void Install() => Installs++;
        public void Restore(BundleOwnershipIdentity ownership) =>
            Restores.Add(ownership);
        public void Remove() => Removals++;
    }

    private sealed class RecordingInstalledUninstallerWriter : IInstalledUninstallerWriter
    {
        public List<InstalledUninstallerWriteRequest> Requests { get; } = [];

        public Task WriteVerifiedAsync(
            InstalledUninstallerWriteRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed record DynamicLightingIdentityRequest(
        string InstallDirectory,
        string UserSid);

    private sealed class RecordingDynamicLightingIdentityRegistration
        : IDynamicLightingIdentityRegistration
    {
        public bool Registered { get; init; }
        public List<DynamicLightingIdentityRequest> Queries { get; } = [];
        public List<DynamicLightingIdentityRequest> Installs { get; } = [];
        public List<DynamicLightingIdentityRequest> Removals { get; } = [];

        public Task<bool> IsRegisteredAsync(
            string installDirectory,
            string userSid,
            CancellationToken cancellationToken)
        {
            Queries.Add(new(installDirectory, userSid));
            return Task.FromResult(Registered);
        }

        public Task InstallIfPresentAsync(
            string installDirectory,
            string userSid,
            CancellationToken cancellationToken)
        {
            Installs.Add(new(installDirectory, userSid));
            return Task.CompletedTask;
        }

        public Task RemoveIfPresentAsync(
            string installDirectory,
            string userSid,
            CancellationToken cancellationToken)
        {
            Removals.Add(new(installDirectory, userSid));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHealthProbe : ISetupServiceHealthProbe
    {
        public int Verifications { get; private set; }

        public Task VerifyAsync(CancellationToken cancellationToken)
        {
            Verifications++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProcessOperations(params ISetupProcess[] processes) : ISetupProcessOperations
    {
        public ISetupProcess GetProcessById(int processId) => Assert.Single(processes, process => process.Id == processId);
        public List<string> Operations { get; } = [];
        public bool SignalResult { get; init; } = true;

        public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName)
        {
            Operations.Add($"query:{processName}");
            return processes;
        }

        public bool TrySignalEvent(string eventName)
        {
            Operations.Add($"signal:{eventName}");
            return SignalResult;
        }
    }

    private sealed class RecordingProcess(string executablePath) : ISetupProcess
    {
        public int Id => 123;
        public bool TryRetainLifetime() => true;
        public string? ExecutablePath => executablePath;
        public bool Disposed { get; private set; }
        public bool WaitedForExit { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitedForExit = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }
}

[CollectionDefinition(nameof(SetupDiagnosticTraceCollection), DisableParallelization = true)]
public sealed class SetupDiagnosticTraceCollection;
