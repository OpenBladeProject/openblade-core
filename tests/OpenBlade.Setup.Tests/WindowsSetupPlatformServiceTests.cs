using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class WindowsSetupPlatformServiceTests
{
    [Fact]
    public async Task InstallService_NewService_RegistersLiteralQuotedImagePath()
    {
        RecordingServiceControl serviceControl = new();
        WindowsSetupPlatform platform = new(serviceControl);

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
        WindowsSetupPlatform platform = new(serviceControl);

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
        WindowsSetupPlatform platform = new(serviceControl);

        await platform.InstallServiceAsync(CancellationToken.None);

        Assert.DoesNotContain(serviceControl.Commands, command => command.Arguments[0] == "create");
        Assert.Contains(serviceControl.Commands, command => command.Arguments[0] == "config");
    }

    [Fact]
    public async Task InstallService_ConfiguresBoundedRestartRecovery()
    {
        RecordingServiceControl serviceControl = new();
        WindowsSetupPlatform platform = new(serviceControl);

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

    [Fact]
    public async Task InstallService_StartsThenWaitsForRunning()
    {
        RecordingServiceControl serviceControl = new();
        WindowsSetupPlatform platform = new(serviceControl);

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
        WindowsSetupPlatform platform = new(serviceControl);

        SetupInstallLifecycleState prior = await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);
        await platform.PrepareServiceForInstallAsync(CancellationToken.None);

        Assert.Equal(new SetupServiceInstallState(true, true), prior.Service);
        Assert.Equal(["run:stop", "wait:Stopped:00:00:30"], serviceControl.Operations);
    }

    [Fact]
    public async Task CaptureThenPrepareService_StoppedService_RecordsStateWithoutStartingOrStoppingIt()
    {
        RecordingServiceControl serviceControl = new() { State = SetupServiceState.Stopped };
        WindowsSetupPlatform platform = new(serviceControl);

        SetupInstallLifecycleState prior = await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);
        await platform.PrepareServiceForInstallAsync(CancellationToken.None);

        Assert.Equal(new SetupServiceInstallState(true, false), prior.Service);
        Assert.Empty(serviceControl.Operations);
    }

    [Fact]
    public async Task CaptureInstallLifecycle_ReadsShortcutExistenceWithoutMutatingIt()
    {
        RecordingFileOperations files = new() { FileExistsResult = true };
        WindowsSetupPlatform platform = new(fileOperations: files);

        SetupInstallLifecycleState prior =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);

        Assert.True(prior.Startup.ShortcutExisted);
        Assert.Empty(files.DeletedFiles);
    }

    [Fact]
    public async Task RestoreServiceInstallState_PriorRunningAndStillRunning_DoesNotStartAgainAndChecksHealth()
    {
        RecordingServiceControl serviceControl = new() { State = SetupServiceState.Running };
        RecordingHealthProbe health = new();
        WindowsSetupPlatform platform = new(serviceControl, healthProbe: health);

        await platform.RestoreServiceInstallStateAsync(new(true, true), CancellationToken.None);

        Assert.DoesNotContain(serviceControl.Commands, command => command.Arguments[0] == "start");
        Assert.Equal(1, health.Verifications);
    }

    [Fact]
    public async Task RestoreServiceInstallState_PriorStoppedAndRegistrationMissing_RecreatesWithoutStarting()
    {
        RecordingServiceControl serviceControl = new();
        RecordingHealthProbe health = new();
        WindowsSetupPlatform platform = new(serviceControl, healthProbe: health);

        await platform.RestoreServiceInstallStateAsync(new(true, false), CancellationToken.None);

        Assert.Contains(serviceControl.Commands, command => command.Arguments[0] == "create");
        Assert.DoesNotContain(serviceControl.Commands, command => command.Arguments[0] == "start");
        Assert.Equal(0, health.Verifications);
    }

    [Fact]
    public async Task StopTray_FreshInstallWithNoTrayProcesses_IsSafe()
    {
        RecordingProcessOperations processes = new();
        WindowsSetupPlatform platform = new(processOperations: processes);

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
        WindowsSetupPlatform platform = new(processOperations: processes);

        await platform.StopTrayAsync(CancellationToken.None);

        Assert.Collection(
            processes.Operations,
            operation => Assert.Equal("query:OpenBlade.Tray", operation),
            operation => Assert.StartsWith("signal:Local\\OpenBlade.Tray.Shutdown.", operation, StringComparison.Ordinal));
        Assert.True(process.WaitedForExit);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RemoveService_StopsWaitsDeletesThenWaitsForDeletion()
    {
        RecordingServiceControl serviceControl = new() { Exists = true };
        WindowsSetupPlatform platform = new(serviceControl);

        await platform.RemoveServiceAsync(CancellationToken.None);

        Assert.Equal(
            ["run:stop", "wait:Stopped:00:00:30", "run:delete", "wait-deleted:00:00:30"],
            serviceControl.Operations);
    }

    [Fact]
    public async Task InstallStartupShortcut_CreatesRealLinkToTrayWithInstallWorkingDirectory()
    {
        RecordingShortcutWriter shortcutWriter = new();
        WindowsSetupPlatform platform = new(new RecordingServiceControl(), shortcutWriter);

        await platform.InstallStartupShortcutAsync(CancellationToken.None);

        string installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade");
        ShortcutRequest request = Assert.Single(shortcutWriter.Requests);
        Assert.EndsWith("OpenBlade.lnk", request.ShortcutPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(installDirectory, "OpenBlade.Tray.exe"), request.TargetPath);
        Assert.Equal(installDirectory, request.WorkingDirectory);
    }

    [Fact]
    public async Task InstallStartupShortcut_RemovesLegacyCommandStartupEntry()
    {
        RecordingFileOperations files = new();
        WindowsSetupPlatform platform = new(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            fileOperations: files);

        await platform.InstallStartupShortcutAsync(CancellationToken.None);

        Assert.Contains(
            files.DeletedFiles,
            path => path.EndsWith("OpenBlade.cmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemoveStartupShortcut_RemovesCurrentAndLegacyStartupEntries()
    {
        RecordingFileOperations files = new();
        WindowsSetupPlatform platform = new(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            fileOperations: files);

        await platform.RemoveStartupShortcutAsync(CancellationToken.None);

        Assert.Collection(
            files.DeletedFiles,
            path => Assert.EndsWith("OpenBlade.lnk", path, StringComparison.OrdinalIgnoreCase),
            path => Assert.EndsWith("OpenBlade.cmd", path, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-1-0")]
    public async Task WriteAuthorizedUserSid_InvalidOrNonAccountSid_FailsClosed(string sid)
    {
        RecordingAuthorizedSidWriter sidWriter = new();
        WindowsSetupPlatform platform = new(
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
        WindowsSetupPlatform platform = new(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            sidWriter);

        await platform.WriteAuthorizedUserSidAsync(
            " S-1-5-21-1-2-3-1001 ",
            CancellationToken.None);

        AuthorizedSidWriteRequest request = Assert.Single(sidWriter.Requests);
        Assert.Equal("S-1-5-21-1-2-3-1001", request.Sid);
        Assert.EndsWith(
            Path.Combine("OpenBlade", "authorized-user.sid"),
            request.Path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAndRemoveService_NeverTargetsFormerServiceIdentity()
    {
        RecordingServiceControl serviceControl = new() { Exists = true };
        WindowsSetupPlatform platform = new(serviceControl);

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

    private sealed class RecordingShortcutWriter : IStartupShortcutWriter
    {
        public List<ShortcutRequest> Requests { get; } = [];

        public Task CreateAsync(ShortcutRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
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

    private sealed class RecordingFileOperations : ISetupFileOperations
    {
        public List<string> DeletedFiles { get; } = [];
        public bool FileExistsResult { get; init; }

        public bool FileExists(string path) => FileExistsResult;

        public void DeleteFile(string path) => DeletedFiles.Add(path);
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
        public List<string> Operations { get; } = [];

        public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName)
        {
            Operations.Add($"query:{processName}");
            return processes;
        }

        public bool TrySignalEvent(string eventName)
        {
            Operations.Add($"signal:{eventName}");
            return true;
        }
    }

    private sealed class RecordingProcess(string executablePath) : ISetupProcess
    {
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
