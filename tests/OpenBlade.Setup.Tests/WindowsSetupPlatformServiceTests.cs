using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class WindowsSetupPlatformServiceTests
{
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

        Assert.True(prior.Startup.TrayShortcutExisted);
        Assert.True(prior.Startup.SessionAgentShortcutExisted);
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

    [Fact]
    public async Task InstallStartupShortcut_CreatesRealLinkToTrayWithInstallWorkingDirectory()
    {
        RecordingShortcutWriter shortcutWriter = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            shortcutWriter);

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
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            fileOperations: files);

        await platform.InstallStartupShortcutAsync(CancellationToken.None);

        Assert.Contains(
            files.DeletedFiles,
            path => path.EndsWith("OpenBlade.cmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallSessionAgentStartupShortcut_CreatesIndependentMandatoryLink()
    {
        RecordingShortcutWriter shortcutWriter = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            shortcutWriter);

        await platform.InstallSessionAgentStartupShortcutAsync(CancellationToken.None);

        string installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade");
        ShortcutRequest request = Assert.Single(shortcutWriter.Requests);
        Assert.EndsWith("OpenBlade.SessionAgent.lnk", request.ShortcutPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(installDirectory, "OpenBlade.SessionAgent.exe"), request.TargetPath);
        Assert.Equal(installDirectory, request.WorkingDirectory);
    }

    [Fact]
    public async Task RemoveStartupShortcut_RemovesCurrentAndLegacyStartupEntries()
    {
        RecordingFileOperations files = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            new RecordingServiceControl(),
            new RecordingShortcutWriter(),
            fileOperations: files);

        await platform.RemoveStartupShortcutAsync(CancellationToken.None);

        Assert.Collection(
            files.DeletedFiles,
            path => Assert.EndsWith("OpenBlade.lnk", path, StringComparison.OrdinalIgnoreCase),
            path => Assert.EndsWith("OpenBlade.cmd", path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemoveSessionAgentStartupShortcut_RemovesOnlyAgentEntry()
    {
        RecordingFileOperations files = new();
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(fileOperations: files);

        await platform.RemoveSessionAgentStartupShortcutAsync(CancellationToken.None);

        Assert.Equal(2, files.DeletedFiles.Count);
        Assert.All(files.DeletedFiles, removed => Assert.EndsWith(
            "OpenBlade.SessionAgent.lnk",
            removed,
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            files.DeletedFiles,
            removed => removed.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                StringComparison.OrdinalIgnoreCase));
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
        RecordingDynamicLightingIdentityRegistration identity = new()
        {
            Registered = true,
        };
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            dynamicLightingIdentityRegistration: identity);

        SetupInstallLifecycleState state =
            await platform.CaptureInstallLifecycleStateAsync(CancellationToken.None);
        await platform.RemoveDynamicLightingIdentityAsync(CancellationToken.None);
        await platform.InstallDynamicLightingIdentityAsync(CancellationToken.None);

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
        Assert.Equal(platform.CurrentUserSid, query.UserSid);
    }

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

    private sealed class RecordingMachineDataDirectorySecurity
        : IMachineDataDirectorySecurity
    {
        public List<string> ProtectedDirectories { get; } = [];
        public Exception? Failure { get; init; }

        public Task ProtectAsync(string directory, CancellationToken cancellationToken)
        {
            ProtectedDirectories.Add(directory);
            return Failure is null
                ? Task.CompletedTask
                : Task.FromException(Failure);
        }
    }

    private sealed class RecordingFileOperations : ISetupFileOperations
    {
        public List<string> DeletedFiles { get; } = [];
        public bool FileExistsResult { get; init; }

        public bool FileExists(string path) => FileExistsResult;

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
