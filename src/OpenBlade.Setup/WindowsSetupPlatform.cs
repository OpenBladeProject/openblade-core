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

public sealed class WindowsSetupPlatform : ISetupPlatform
{
    private const string ServiceName = "OpenBlade";
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

    public WindowsSetupPlatform(
        ISetupServiceControl? serviceControl = null,
        IStartupShortcutWriter? shortcutWriter = null,
        IAuthorizedUserSidWriter? sidWriter = null,
        ISetupServiceHealthProbe? healthProbe = null,
        ISetupFileOperations? fileOperations = null,
        ISetupProcessOperations? processOperations = null,
        SetupPaths? paths = null,
        string? payloadDirectory = null,
        ISetupPayloadFileSystem? payloadFileSystem = null,
        IMachineDataDirectorySecurity? machineDataDirectorySecurity = null)
    {
        this.paths = paths ?? SetupPaths.Create(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        this.payloadDirectory = payloadDirectory ?? Path.Combine(AppContext.BaseDirectory, "payload");
        this.payloadFileSystem = payloadFileSystem ?? new WindowsSetupPayloadFileSystem();
        this.serviceControl = serviceControl ?? new ScSetupServiceControl();
        this.shortcutWriter = shortcutWriter ?? new WindowsStartupShortcutWriter();
        this.sidWriter = sidWriter ?? new AtomicAuthorizedUserSidWriter();
        this.healthProbe = healthProbe ?? new SetupServiceHealthProbe();
        this.fileOperations = fileOperations ?? new WindowsSetupFileOperations();
        this.processOperations = processOperations ?? new WindowsSetupProcessOperations();
        this.machineDataDirectorySecurity = machineDataDirectorySecurity
            ?? new WindowsMachineDataDirectorySecurity();
    }

    public string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The installing user's SID is unavailable.");

    public async Task<SetupInstallLifecycleState> CaptureInstallLifecycleStateAsync(
        CancellationToken cancellationToken)
    {
        SetupServiceState? state = await serviceControl.GetStateAsync(ServiceName, cancellationToken);
        bool trayShortcutExisted = fileOperations.FileExists(paths.StartupFile)
            || fileOperations.FileExists(paths.LegacyStartupFile);
        bool sessionAgentShortcutExisted = fileOperations.FileExists(paths.SessionAgentStartupFile);
        return new(
            new(state is not null, state == SetupServiceState.Running),
            new(trayShortcutExisted, sessionAgentShortcutExisted));
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
        SetupPayloadTransaction.StageAsync(paths, payloadDirectory, payloadFileSystem, cancellationToken);

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
            new ShortcutRequest(paths.StartupFile, trayExecutable, paths.InstallDirectory),
            cancellationToken);
        fileOperations.DeleteFile(paths.LegacyStartupFile);
    }

    public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.DeleteFile(paths.StartupFile);
        fileOperations.DeleteFile(paths.LegacyStartupFile);
        return Task.CompletedTask;
    }

    public Task InstallSessionAgentStartupShortcutAsync(CancellationToken cancellationToken)
    {
        string sessionAgentExecutable = Path.Combine(
            paths.InstallDirectory,
            "OpenBlade.SessionAgent.exe");
        return shortcutWriter.CreateAsync(
            new ShortcutRequest(
                paths.SessionAgentStartupFile,
                sessionAgentExecutable,
                paths.InstallDirectory),
            cancellationToken);
    }

    public Task RemoveSessionAgentStartupShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.DeleteFile(paths.SessionAgentStartupFile);
        return Task.CompletedTask;
    }

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
