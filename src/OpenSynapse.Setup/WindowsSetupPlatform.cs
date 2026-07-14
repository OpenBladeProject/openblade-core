using System.Diagnostics;
using System.Security.Principal;

namespace OpenSynapse.Setup;

public sealed class WindowsSetupPlatform : ISetupPlatform
{
    private const string ServiceName = "OpenSynapse";
    private readonly string installDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSynapse");
    private readonly string machineDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenSynapse");
    private readonly string userDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSynapse");
    private readonly string startupFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "OpenSynapse.cmd");

    public string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The installing user's SID is unavailable.");

    public async Task CopyPayloadAsync(CancellationToken cancellationToken)
    {
        string source = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "payload"))
            ? Path.Combine(AppContext.BaseDirectory, "payload")
            : AppContext.BaseDirectory;
        Directory.CreateDirectory(installDirectory);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(installDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        await Task.CompletedTask;
    }

    public async Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(machineDataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(machineDataDirectory, "authorized-user.sid"), sid, cancellationToken);
    }

    public async Task InstallServiceAsync(CancellationToken cancellationToken)
    {
        string serviceExecutable = Path.Combine(installDirectory, "OpenSynapse.Service.exe");
        await RunScAsync(
            ["create", ServiceName, "binPath=", serviceExecutable, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "OpenSynapse Service"],
            false,
            cancellationToken);
        await RunScAsync(["description", ServiceName, "OpenSynapse hardware control and profile service"], false, cancellationToken);
        await RunScAsync(["start", ServiceName], false, cancellationToken);
    }

    public async Task InstallStartupShortcutAsync(CancellationToken cancellationToken)
    {
        string trayExecutable = Path.Combine(installDirectory, "OpenSynapse.Tray.exe");
        string contents = $"@echo off{Environment.NewLine}start \"\" \"{trayExecutable}\"{Environment.NewLine}";
        await File.WriteAllTextAsync(startupFile, contents, cancellationToken);
    }

    public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(startupFile);
        return Task.CompletedTask;
    }

    public async Task RemoveServiceAsync(CancellationToken cancellationToken)
    {
        await RunScAsync(["stop", ServiceName], true, cancellationToken);
        await RunScAsync(["delete", ServiceName], true, cancellationToken);
    }

    public Task RemoveBinariesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(installDirectory))
        {
            Directory.Delete(installDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task RemoveDataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteIfPresent(machineDataDirectory);
        DeleteIfPresent(userDataDirectory);
        return Task.CompletedTask;
    }

    private static void DeleteIfPresent(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunScAsync(
        IReadOnlyList<string> arguments,
        bool tolerateFailure,
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
        if (process.ExitCode != 0 && !tolerateFailure)
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Service Control failed ({process.ExitCode}): {error}{output}".Trim());
        }
    }
}
