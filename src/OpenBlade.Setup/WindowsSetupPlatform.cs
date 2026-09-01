using System.Reflection;
using System.Security.Principal;
using OpenBlade.Contracts;

namespace OpenBlade.Setup;

public interface ISetupFileOperations
{
    bool FileExists(string path);
    void DeleteFile(string path);
}

internal sealed class WindowsSetupFileOperations : ISetupFileOperations
{
    public bool FileExists(string path) => File.Exists(path);
    public void DeleteFile(string path) => File.Delete(path);
}

public sealed record WindowsSetupPlatformOptions(
    ISetupServiceControl? ServiceControl = null,
    IStartupShortcutWriter? ShortcutWriter = null,
    IAuthorizedUserSidWriter? SidWriter = null,
    ISetupServiceHealthProbe? HealthProbe = null,
    ISetupFileOperations? FileOperations = null,
    ISetupProcessOperations? ProcessOperations = null,
    SetupPaths? Paths = null,
    string? PayloadDirectory = null,
    ISetupPayloadFileSystem? PayloadFileSystem = null,
    IMachineDataDirectorySecurity? MachineDataDirectorySecurity = null,
    IUninstallRegistration? UninstallRegistration = null,
    IBundleOwnershipRegistration? BundleOwnershipRegistration = null,
    IInstalledUninstallerWriter? InstalledUninstallerWriter = null,
    IDynamicLightingIdentityRegistration? DynamicLightingIdentityRegistration = null,
    string? SetupExecutablePath = null,
    ISetupProgressReporter? ProgressReporter = null,
    string? BundleOwnerId = null,
    string? CurrentUserSid = null);

public sealed class WindowsSetupPlatform : ISetupPlatform
{
    private const string ServiceName = OpenBladeWindowsService.Name;
    private const int ServiceDoesNotExist = 1060;
    private const int ServiceNotActive = 1062;
    private readonly SetupPaths paths;
    private readonly string payloadDirectory;
    private readonly ISetupPayloadFileSystem payloadFileSystem;
    private readonly ISetupServiceControl serviceControl;
    private readonly IStartupShortcutWriter shortcutWriter;
    private readonly IAuthorizedUserSidWriter sidWriter;
    private readonly ISetupServiceHealthProbe healthProbe;
    private readonly ISetupFileOperations fileOperations;
    private readonly ISetupProcessOperations processOperations;
    private readonly IMachineDataDirectorySecurity machineDataDirectorySecurity;
    private readonly IUninstallRegistration uninstallRegistration;
    private readonly IBundleOwnershipRegistration bundleOwnershipRegistration;
    private readonly IInstalledUninstallerWriter installedUninstallerWriter;
    private readonly IDynamicLightingIdentityRegistration dynamicLightingIdentityRegistration;
    private readonly ISetupProgressReporter progressReporter;
    private readonly string setupExecutablePath;
    private readonly string currentUserSid;

    public WindowsSetupPlatform(WindowsSetupPlatformOptions? options = null)
    {
        options ??= new();
        paths = options.Paths ?? SetupPaths.CreateForCurrentIdentity();
        payloadDirectory = options.PayloadDirectory ?? Path.Combine(AppContext.BaseDirectory, "payload");
        payloadFileSystem = options.PayloadFileSystem ?? new WindowsSetupPayloadFileSystem();
        serviceControl = options.ServiceControl ?? new ScSetupServiceControl();
        shortcutWriter = options.ShortcutWriter ?? new WindowsStartupShortcutWriter();
        sidWriter = options.SidWriter ?? new AtomicAuthorizedUserSidWriter();
        healthProbe = options.HealthProbe ?? new SetupServiceHealthProbe();
        fileOperations = options.FileOperations ?? new WindowsSetupFileOperations();
        processOperations = options.ProcessOperations ?? new WindowsSetupProcessOperations();
        machineDataDirectorySecurity = options.MachineDataDirectorySecurity
            ?? new WindowsMachineDataDirectorySecurity();
        uninstallRegistration = options.UninstallRegistration ?? new WindowsUninstallRegistration();
        bundleOwnershipRegistration = options.BundleOwnershipRegistration
            ?? new WindowsBundleOwnershipRegistration(options.BundleOwnerId);
        installedUninstallerWriter = options.InstalledUninstallerWriter
            ?? new VerifiedInstalledUninstallerWriter();
        dynamicLightingIdentityRegistration = options.DynamicLightingIdentityRegistration
            ?? new DynamicLightingIdentityRegistration();
        progressReporter = options.ProgressReporter ?? NullSetupProgressReporter.Instance;
        setupExecutablePath = options.SetupExecutablePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The OpenBlade setup executable path is unavailable.");
        currentUserSid = ResolveCurrentUserSid(options.CurrentUserSid);
    }

    public string CurrentUserSid => currentUserSid;

    internal static string ResolveCurrentUserSid(string? enrolledUserSid)
    {
        if (enrolledUserSid is null)
        {
            return WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The installing user's SID is unavailable.");
        }

        try
        {
            SecurityIdentifier identity = new(enrolledUserSid);
            if (!identity.IsAccountSid()
                || identity.AccountDomainSid is null
                || !string.Equals(identity.Value, enrolledUserSid, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The local-development enrollment must contain a canonical account SID.");
            }

            return identity.Value;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The local-development enrollment contains an invalid account SID.",
                exception);
        }
    }

    public async Task<SetupInstallLifecycleState> CaptureInstallLifecycleStateAsync(
        CancellationToken cancellationToken)
    {
        SetupServiceState? state = await serviceControl.GetStateAsync(ServiceName, cancellationToken);
        bool trayShortcutExisted = fileOperations.FileExists(paths.StartupFile)
            || fileOperations.FileExists(paths.LegacyTrayStartupFile)
            || fileOperations.FileExists(paths.LegacyStartupFile);
        bool sessionAgentShortcutExisted = fileOperations.FileExists(paths.SessionAgentStartupFile)
            || fileOperations.FileExists(paths.LegacySessionAgentStartupFile);
        bool startMenuShortcutExisted = fileOperations.FileExists(paths.StartMenuFile);
        bool uninstallShortcutExisted = fileOperations.FileExists(paths.UninstallStartMenuFile);
        bool uninstallRegistrationExisted = uninstallRegistration.Exists();
        BundleOwnershipIdentity? bundleOwnership =
            bundleOwnershipRegistration.Capture();
        bool dynamicLightingIdentityRegistered =
            await dynamicLightingIdentityRegistration.IsRegisteredAsync(
                paths.InstallDirectory,
                CurrentUserSid,
                cancellationToken);
        return new(
            new(state is not null, state == SetupServiceState.Running),
            new(
                trayShortcutExisted,
                sessionAgentShortcutExisted,
                startMenuShortcutExisted,
                uninstallShortcutExisted,
                uninstallRegistrationExisted),
            bundleOwnership is not null,
            dynamicLightingIdentityRegistered,
            bundleOwnership is null
                || bundleOwnershipRegistration.IsOwnedByCurrentBuild(bundleOwnership))
        {
            BundleOwnership = bundleOwnership,
        };
    }

    public async Task PrepareServiceForInstallAsync(CancellationToken cancellationToken)
    {
        SetupServiceState? state = await serviceControl.GetStateAsync(ServiceName, cancellationToken);

        if (state == SetupServiceState.Running)
        {
            await serviceControl.RunAsync(
                ["stop", ServiceName],
                [ServiceNotActive],
                cancellationToken);
            await serviceControl.WaitForStateAsync(
                ServiceName,
                SetupServiceState.Stopped,
                TimeSpan.FromSeconds(30),
                cancellationToken);
        }

    }

    public Task<ISetupPayloadTransaction> StagePayloadAsync(CancellationToken cancellationToken) =>
        SetupPayloadTransaction.StageAsync(
            paths,
            payloadDirectory,
            payloadFileSystem,
            cancellationToken,
            progressReporter);

    public async Task RestoreServiceInstallStateAsync(
        SetupServiceInstallState priorState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        if (!priorState.Existed)
        {
            await RemoveServiceAsync(cancellationToken);
            return;
        }

        SetupServiceState? current = await serviceControl.GetStateAsync(ServiceName, cancellationToken);
        if (priorState.WasRunning)
        {
            if (current != SetupServiceState.Running)
            {
                await ConfigureServiceAsync(startService: true, cancellationToken);
            }

            await VerifyServiceHealthAsync(cancellationToken);
            return;
        }

        if (current is null)
        {
            await ConfigureServiceAsync(startService: false, cancellationToken);
        }
        else if (current == SetupServiceState.Running)
        {
            await serviceControl.RunAsync(
                ["stop", ServiceName],
                [ServiceNotActive],
                cancellationToken);
            await serviceControl.WaitForStateAsync(
                ServiceName,
                SetupServiceState.Stopped,
                TimeSpan.FromSeconds(30),
                cancellationToken);
        }
    }

    public async Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken)
    {
        SecurityIdentifier identity;
        try
        {
            identity = new SecurityIdentifier(sid?.Trim() ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("A valid Windows account SID is required.", nameof(sid), exception);
        }

        if (!identity.IsAccountSid())
        {
            throw new ArgumentException("A valid Windows account SID is required.", nameof(sid));
        }

        if (!SetupPaths.IsExactOpenBladeDirectory(
            paths.MachineDataDirectory,
            paths.MachineDataRoot))
        {
            throw new InvalidOperationException(
                $"Refusing to secure unexpected machine data path '{paths.MachineDataDirectory}'.");
        }

        await machineDataDirectorySecurity.ProtectAsync(
            paths.MachineDataDirectory,
            cancellationToken);
        await sidWriter.WriteAsync(
            new AuthorizedSidWriteRequest(
                Path.Combine(paths.MachineDataDirectory, "authorized-user.sid"),
                identity.Value),
            cancellationToken);
    }

    public Task InstallServiceAsync(CancellationToken cancellationToken) =>
        ConfigureServiceAsync(startService: true, cancellationToken);

    public Task InstallUninstallerAsync(CancellationToken cancellationToken) =>
        installedUninstallerWriter.WriteVerifiedAsync(
            new InstalledUninstallerWriteRequest(
                setupExecutablePath,
                Path.Combine(paths.InstallDirectory, "OpenBlade.Setup.exe")),
            cancellationToken);

    private async Task ConfigureServiceAsync(
        bool startService,
        CancellationToken cancellationToken)
    {
        string serviceExecutable = Path.Combine(paths.InstallDirectory, "OpenBlade.Service.exe");
        bool serviceExists = await serviceControl.ExistsAsync(ServiceName, cancellationToken);
        await serviceControl.RunAsync(
            [serviceExists ? "config" : "create", ServiceName, "binPath=", $"\"{serviceExecutable}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "OpenBlade Service"],
            Array.Empty<int>(),
            cancellationToken);
        await serviceControl.RunAsync(["description", ServiceName, "OpenBlade hardware control and profile service"], Array.Empty<int>(), cancellationToken);
        await serviceControl.RunAsync(
            ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"],
            Array.Empty<int>(),
            cancellationToken);
        await serviceControl.RunAsync(["failureflag", ServiceName, "1"], Array.Empty<int>(), cancellationToken);
        if (startService)
        {
            await serviceControl.RunAsync(["start", ServiceName], Array.Empty<int>(), cancellationToken);
            await serviceControl.WaitForStateAsync(
                ServiceName,
                SetupServiceState.Running,
                TimeSpan.FromSeconds(30),
                cancellationToken);
        }
    }

    public async Task VerifyServiceHealthAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await healthProbe.VerifyAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The OpenBlade service did not respond to its health check within 10 seconds.", exception);
        }
    }

    public async Task InstallStartupShortcutAsync(CancellationToken cancellationToken)
    {
        string trayExecutable = Path.Combine(paths.InstallDirectory, "OpenBlade.Tray.exe");
        await shortcutWriter.CreateAsync(
            new ShortcutRequest(
                paths.StartupFile,
                trayExecutable,
                paths.InstallDirectory,
                Arguments: StartupShortcutArguments.Tray),
            cancellationToken);
        if (!string.Equals(
                paths.LegacyTrayStartupFile,
                paths.StartupFile,
                StringComparison.OrdinalIgnoreCase))
        {
            fileOperations.DeleteFile(paths.LegacyTrayStartupFile);
        }
        fileOperations.DeleteFile(paths.LegacyStartupFile);
    }

    public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.DeleteFile(paths.StartupFile);
        if (!string.Equals(
                paths.LegacyTrayStartupFile,
                paths.StartupFile,
                StringComparison.OrdinalIgnoreCase))
        {
            fileOperations.DeleteFile(paths.LegacyTrayStartupFile);
        }
        fileOperations.DeleteFile(paths.LegacyStartupFile);
        return Task.CompletedTask;
    }

    public async Task InstallSessionAgentStartupShortcutAsync(
        CancellationToken cancellationToken)
    {
        string sessionAgentExecutable = Path.Combine(
            paths.InstallDirectory,
            "OpenBlade.SessionAgent.exe");
        await shortcutWriter.CreateAsync(
            new ShortcutRequest(
                paths.SessionAgentStartupFile,
                sessionAgentExecutable,
                paths.InstallDirectory),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                paths.LegacySessionAgentStartupFile,
                paths.SessionAgentStartupFile,
                StringComparison.OrdinalIgnoreCase))
        {
            fileOperations.DeleteFile(paths.LegacySessionAgentStartupFile);
        }
    }

    public Task RemoveSessionAgentStartupShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.DeleteFile(paths.SessionAgentStartupFile);
        if (!string.Equals(
                paths.LegacySessionAgentStartupFile,
                paths.SessionAgentStartupFile,
                StringComparison.OrdinalIgnoreCase))
        {
            fileOperations.DeleteFile(paths.LegacySessionAgentStartupFile);
        }
        return Task.CompletedTask;
    }

    public Task InstallStartMenuShortcutAsync(CancellationToken cancellationToken)
    {
        string trayExecutable = Path.Combine(paths.InstallDirectory, "OpenBlade.Tray.exe");
        return shortcutWriter.CreateAsync(
            new ShortcutRequest(
                paths.StartMenuFile,
                trayExecutable,
                paths.InstallDirectory),
            cancellationToken);
    }

    public Task RemoveStartMenuShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.DeleteFile(paths.StartMenuFile);
        return Task.CompletedTask;
    }

    public Task InstallUninstallShortcutAsync(CancellationToken cancellationToken)
    {
        string setupExecutable = Path.Combine(paths.InstallDirectory, "OpenBlade.Setup.exe");
        return shortcutWriter.CreateAsync(
            new ShortcutRequest(
                paths.UninstallStartMenuFile,
                setupExecutable,
                paths.InstallDirectory,
                "uninstall --interactive",
                SetupStrings.Get("Uninstall OpenBlade and keep saved profiles"),
                WindowStyle: 7),
            cancellationToken);
    }

    public Task RemoveUninstallShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.DeleteFile(paths.UninstallStartMenuFile);
        return Task.CompletedTask;
    }

    public Task InstallUninstallRegistrationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string setupExecutable = Path.Combine(paths.InstallDirectory, "OpenBlade.Setup.exe");
        string trayExecutable = Path.Combine(paths.InstallDirectory, "OpenBlade.Tray.exe");
        string displayVersion = typeof(WindowsSetupPlatform).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(WindowsSetupPlatform).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        uninstallRegistration.Install(
            new UninstallRegistrationRequest(
                displayVersion,
                paths.InstallDirectory,
                trayExecutable,
                $"\"{setupExecutable}\" uninstall --interactive"));
        return Task.CompletedTask;
    }

    public Task RemoveUninstallRegistrationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uninstallRegistration.Remove();
        return Task.CompletedTask;
    }

    public Task InstallBundleOwnershipAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bundleOwnershipRegistration.Install();
        return Task.CompletedTask;
    }

    public Task RestoreBundleOwnershipAsync(
        BundleOwnershipIdentity ownership,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bundleOwnershipRegistration.Restore(ownership);
        return Task.CompletedTask;
    }

    public Task RemoveBundleOwnershipAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bundleOwnershipRegistration.Remove();
        return Task.CompletedTask;
    }

    public Task RestoreDynamicLightingIdentityAsync(CancellationToken cancellationToken) =>
        dynamicLightingIdentityRegistration.InstallIfPresentAsync(
            paths.InstallDirectory,
            CurrentUserSid,
            cancellationToken);

    public Task RemoveDynamicLightingIdentityAsync(CancellationToken cancellationToken) =>
        dynamicLightingIdentityRegistration.RemoveIfPresentAsync(
            paths.InstallDirectory,
            CurrentUserSid,
            cancellationToken);

    public async Task RemoveServiceAsync(CancellationToken cancellationToken)
    {
        if (!await serviceControl.ExistsAsync(ServiceName, cancellationToken))
        {
            return;
        }

        await serviceControl.RunAsync(["stop", ServiceName], [ServiceDoesNotExist, ServiceNotActive], cancellationToken);
        await serviceControl.WaitForStateAsync(
            ServiceName,
            SetupServiceState.Stopped,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        await serviceControl.RunAsync(["delete", ServiceName], [ServiceDoesNotExist], cancellationToken);
        await serviceControl.WaitForDeletionAsync(ServiceName, TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task StopTrayAsync(CancellationToken cancellationToken)
    {
        string installedTray = Path.Combine(paths.InstallDirectory, "OpenBlade.Tray.exe");
        return StopInstalledProcessAsync(
            "OpenBlade.Tray",
            installedTray,
            TrayShutdownEndpoint.ForExecutablePath(installedTray),
            "tray",
            cancellationToken);
    }

    public Task StopSessionAgentAsync(CancellationToken cancellationToken)
    {
        string installedAgent = Path.Combine(paths.InstallDirectory, "OpenBlade.SessionAgent.exe");
        return StopInstalledProcessAsync(
            "OpenBlade.SessionAgent",
            installedAgent,
            SessionAgentShutdownEndpoint.ForExecutablePath(installedAgent),
            "session agent",
            cancellationToken);
    }

    private async Task StopInstalledProcessAsync(
        string processName,
        string installedExecutable,
        string shutdownEventName,
        string componentName,
        CancellationToken cancellationToken)
    {
        List<ISetupProcess> installedProcesses = [];
        foreach (ISetupProcess process in processOperations.GetProcessesByName(processName))
        {
            try
            {
                if (TrayProcessIdentity.IsExactInstalledExecutable(
                    process.ExecutablePath,
                    installedExecutable))
                {
                    installedProcesses.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                process.Dispose();
            }
        }

        if (installedProcesses.Count == 0)
        {
            return;
        }

        if (!processOperations.TrySignalEvent(shutdownEventName))
        {
            installedProcesses.ForEach(process => process.Dispose());
            throw new InvalidOperationException(
                $"The installed {componentName} process does not expose its shutdown event.");
        }

        try
        {
            foreach (ISetupProcess process in installedProcesses)
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"The installed {componentName} process did not exit; binaries were not removed.",
                exception);
        }
        finally
        {
            installedProcesses.ForEach(process => process.Dispose());
        }
    }

    public Task RemoveBinariesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteOwnedDirectoryIfPresent(paths.InstallDirectory, paths.ProgramFilesRoot);

        return Task.CompletedTask;
    }

    public Task RemoveDataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteOwnedDirectoryIfPresent(paths.MachineDataDirectory, paths.MachineDataRoot);
        DeleteOwnedDirectoryIfPresent(paths.UserDataDirectory, paths.UserDataRoot);
        return Task.CompletedTask;
    }

    private static void DeleteOwnedDirectoryIfPresent(string directory, string knownFolderRoot)
    {
        if (!SetupPaths.IsExactOpenBladeDirectory(directory, knownFolderRoot))
        {
            throw new InvalidOperationException($"Refusing to recursively delete unexpected path '{directory}'.");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

}
