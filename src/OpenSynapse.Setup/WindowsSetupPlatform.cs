using System.Diagnostics;
using System.Security.Principal;
using OpenSynapse.Contracts;

namespace OpenSynapse.Setup;

public sealed class WindowsSetupPlatform : ISetupPlatform
{
    private const string ServiceName = "OpenSynapse";
    private const int ServiceDoesNotExist = 1060;
    private const int ServiceNotActive = 1062;
    private readonly SetupPaths paths = SetupPaths.Create(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Startup));
    private readonly ISetupServiceControl serviceControl;
    private readonly IStartupShortcutWriter shortcutWriter;
    private readonly IAuthorizedUserSidWriter sidWriter;

    public WindowsSetupPlatform(
        ISetupServiceControl? serviceControl = null,
        IStartupShortcutWriter? shortcutWriter = null,
        IAuthorizedUserSidWriter? sidWriter = null)
    {
        this.serviceControl = serviceControl ?? new ScSetupServiceControl();
        this.shortcutWriter = shortcutWriter ?? new WindowsStartupShortcutWriter();
        this.sidWriter = sidWriter ?? new AtomicAuthorizedUserSidWriter();
    }

    public string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The installing user's SID is unavailable.");

    public async Task PrepareServiceForInstallAsync(CancellationToken cancellationToken)
    {
        if (!await serviceControl.ExistsAsync(ServiceName, cancellationToken))
        {
            return;
        }

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

    public async Task CopyPayloadAsync(CancellationToken cancellationToken)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "payload");
        RequirePayloadFile(source, "OpenSynapse.Service.exe");
        RequirePayloadFile(source, "OpenSynapse.Tray.exe");
        Directory.CreateDirectory(paths.InstallDirectory);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(paths.InstallDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        await Task.CompletedTask;
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

        await sidWriter.WriteAsync(
            new AuthorizedSidWriteRequest(
                Path.Combine(paths.MachineDataDirectory, "authorized-user.sid"),
                identity.Value),
            cancellationToken);
    }

    public async Task InstallServiceAsync(CancellationToken cancellationToken)
    {
        string serviceExecutable = Path.Combine(paths.InstallDirectory, "OpenSynapse.Service.exe");
        bool serviceExists = await serviceControl.ExistsAsync(ServiceName, cancellationToken);
        await serviceControl.RunAsync(
            [serviceExists ? "config" : "create", ServiceName, "binPath=", $"\"{serviceExecutable}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "OpenSynapse Service"],
            Array.Empty<int>(),
            cancellationToken);
        await serviceControl.RunAsync(["description", ServiceName, "OpenSynapse hardware control and profile service"], Array.Empty<int>(), cancellationToken);
        await serviceControl.RunAsync(
            ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"],
            Array.Empty<int>(),
            cancellationToken);
        await serviceControl.RunAsync(["failureflag", ServiceName, "1"], Array.Empty<int>(), cancellationToken);
        await serviceControl.RunAsync(["start", ServiceName], Array.Empty<int>(), cancellationToken);
        await serviceControl.WaitForStateAsync(
            ServiceName,
            SetupServiceState.Running,
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public async Task InstallStartupShortcutAsync(CancellationToken cancellationToken)
    {
        string trayExecutable = Path.Combine(paths.InstallDirectory, "OpenSynapse.Tray.exe");
        await shortcutWriter.CreateAsync(
            new ShortcutRequest(paths.StartupFile, trayExecutable, paths.InstallDirectory),
            cancellationToken);
    }

    public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(paths.StartupFile);
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

    public async Task StopTrayAsync(CancellationToken cancellationToken)
    {
        string installedTray = Path.Combine(paths.InstallDirectory, "OpenSynapse.Tray.exe");
        List<Process> installedProcesses = [];
        foreach (Process process in Process.GetProcessesByName("OpenSynapse.Tray"))
        {
            try
            {
                if (TrayProcessIdentity.IsInstalledTray(process.MainModule?.FileName, installedTray))
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

        string shutdownEventName = TrayShutdownEndpoint.ForExecutablePath(installedTray);
        if (!EventWaitHandle.TryOpenExisting(shutdownEventName, out EventWaitHandle? shutdownEvent))
        {
            installedProcesses.ForEach(process => process.Dispose());
            throw new InvalidOperationException("The installed tray process does not expose its shutdown event.");
        }

        using (shutdownEvent)
        {
            shutdownEvent.Set();
        }

        try
        {
            foreach (Process process in installedProcesses)
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("The installed tray process did not exit; binaries were not removed.", exception);
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
        if (!SetupPaths.IsExactOpenSynapseDirectory(directory, knownFolderRoot))
        {
            throw new InvalidOperationException($"Refusing to recursively delete unexpected path '{directory}'.");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RequirePayloadFile(string payloadDirectory, string fileName)
    {
        string path = Path.Combine(payloadDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The setup payload is incomplete: '{fileName}' is missing.");
        }
    }

}
