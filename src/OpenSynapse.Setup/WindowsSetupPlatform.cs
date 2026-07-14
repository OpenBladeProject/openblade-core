using System.Diagnostics;
using System.Security.Principal;

namespace OpenSynapse.Setup;

public sealed class WindowsSetupPlatform : ISetupPlatform
{
    private const string ServiceName = "OpenSynapse";
    private const int ServiceDoesNotExist = 1060;
    private const int ServiceNotActive = 1062;
    private const string TrayShutdownEventName = @"Local\OpenSynapse.Tray.Shutdown";
    private readonly SetupPaths paths = SetupPaths.Create(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Startup));

    public string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The installing user's SID is unavailable.");

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
        Directory.CreateDirectory(paths.MachineDataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.MachineDataDirectory, "authorized-user.sid"), sid, cancellationToken);
    }

    public async Task InstallServiceAsync(CancellationToken cancellationToken)
    {
        string serviceExecutable = Path.Combine(paths.InstallDirectory, "OpenSynapse.Service.exe");
        await RunScAsync(
            ["create", ServiceName, "binPath=", serviceExecutable, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "OpenSynapse Service"],
            Array.Empty<int>(),
            cancellationToken);
        await RunScAsync(["description", ServiceName, "OpenSynapse hardware control and profile service"], Array.Empty<int>(), cancellationToken);
        await RunScAsync(["start", ServiceName], Array.Empty<int>(), cancellationToken);
    }

    public async Task InstallStartupShortcutAsync(CancellationToken cancellationToken)
    {
        string trayExecutable = Path.Combine(paths.InstallDirectory, "OpenSynapse.Tray.exe");
        string contents = $"@echo off{Environment.NewLine}start \"\" \"{trayExecutable}\"{Environment.NewLine}";
        Directory.CreateDirectory(paths.StartupRoot);
        await File.WriteAllTextAsync(paths.StartupFile, contents, cancellationToken);
    }

    public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(paths.StartupFile);
        return Task.CompletedTask;
    }

    public async Task RemoveServiceAsync(CancellationToken cancellationToken)
    {
        await RunScAsync(["stop", ServiceName], [ServiceDoesNotExist, ServiceNotActive], cancellationToken);
        await RunScAsync(["delete", ServiceName], [ServiceDoesNotExist], cancellationToken);
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

        if (!EventWaitHandle.TryOpenExisting(TrayShutdownEventName, out EventWaitHandle? shutdownEvent))
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

    private static async Task RunScAsync(
        IReadOnlyList<string> arguments,
        int[] toleratedExitCodes,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Windows Service Control.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 && !toleratedExitCodes.Contains(process.ExitCode))
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Service Control failed ({process.ExitCode}): {error}{output}".Trim());
        }
    }
}
