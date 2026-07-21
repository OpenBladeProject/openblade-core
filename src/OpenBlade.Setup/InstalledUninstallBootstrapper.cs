using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenBlade.Setup;

public static class InstalledUninstallBootstrapper
{
    private const string ParentProcessIdEnvironmentVariable = "OPENBLADE_UNINSTALL_PARENT_PID";
    private const string InteractiveRelayEnvironmentVariable = "OPENBLADE_UNINSTALL_INTERACTIVE";
    private const MoveFileFlags DelayUntilReboot = MoveFileFlags.DelayUntilReboot;

    public static bool IsInstalledUninstallInvocation(
        IReadOnlyList<string> arguments,
        string? executablePath,
        SetupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(paths);
        if (!SetupCommandParser.TryParse(arguments, out SetupCommand command)
            || command is not (SetupCommand.Uninstall or SetupCommand.UninstallAndPurge)
            || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        string expected = Path.Combine(paths.InstallDirectory, "OpenBlade.Setup.exe");
        try
        {
            return string.Equals(
                Path.GetFullPath(executablePath),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsRelayExecutable(string? executablePath, SetupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(executablePath);
            return string.Equals(
                    Path.GetFileName(fullPath),
                    "OpenBlade.Setup.exe",
                    StringComparison.OrdinalIgnoreCase)
                && SetupPaths.IsExactUninstallRelayDirectory(
                    Path.GetDirectoryName(fullPath) ?? string.Empty,
                    paths.ProgramFilesRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static void RelaunchFromSecureRelay(
        IReadOnlyList<string> arguments,
        string executablePath,
        SetupPaths paths,
        bool interactive)
    {
        if (!IsInstalledUninstallInvocation(arguments, executablePath, paths))
        {
            throw new InvalidOperationException("Only the installed OpenBlade uninstaller can use the uninstall relay.");
        }

        string relayDirectory = Path.Combine(
            paths.ProgramFilesRoot,
            $"OpenBlade.uninstall.{Guid.NewGuid():N}");
        if (!SetupPaths.IsExactUninstallRelayDirectory(relayDirectory, paths.ProgramFilesRoot))
        {
            throw new InvalidOperationException("Refusing to create an unexpected uninstall relay directory.");
        }

        Directory.CreateDirectory(relayDirectory);
        string relayExecutable = Path.Combine(relayDirectory, "OpenBlade.Setup.exe");
        try
        {
            File.Copy(executablePath, relayExecutable, overwrite: false);
        }
        catch
        {
            TryDeleteRelay(relayDirectory);
            throw;
        }

        ProcessStartInfo startInfo = new(relayExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = relayDirectory,
            CreateNoWindow = interactive,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment[ParentProcessIdEnvironmentVariable] = Environment.ProcessId.ToString();
        if (interactive)
        {
            startInfo.Environment[InteractiveRelayEnvironmentVariable] = "1";
        }

        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            TryDeleteRelay(relayDirectory);
            throw;
        }
    }

    public static bool ConsumeInteractiveRelayMarker()
    {
        bool interactive = string.Equals(
            Environment.GetEnvironmentVariable(InteractiveRelayEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        Environment.SetEnvironmentVariable(InteractiveRelayEnvironmentVariable, null);
        return interactive;
    }

    public static async Task WaitForInstalledParentExitAsync(CancellationToken cancellationToken)
    {
        string? value = Environment.GetEnvironmentVariable(ParentProcessIdEnvironmentVariable);
        Environment.SetEnvironmentVariable(ParentProcessIdEnvironmentVariable, null);
        if (!int.TryParse(value, out int processId) || processId <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (ArgumentException)
        {
            // The installed launcher has already exited.
        }
    }

    public static void ScheduleRelayCleanup(string executablePath, SetupPaths paths)
    {
        if (!IsRelayExecutable(executablePath, paths))
        {
            return;
        }

        string relayDirectory = Path.GetDirectoryName(executablePath)!;
        _ = MoveFileEx(executablePath, null, DelayUntilReboot);
        _ = MoveFileEx(relayDirectory, null, DelayUntilReboot);
    }

    private static void TryDeleteRelay(string relayDirectory)
    {
        try
        {
            Directory.Delete(relayDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            string relayExecutable = Path.Combine(relayDirectory, "OpenBlade.Setup.exe");
            _ = MoveFileEx(relayExecutable, null, DelayUntilReboot);
            _ = MoveFileEx(relayDirectory, null, DelayUntilReboot);
        }
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        DelayUntilReboot = 0x00000004,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string? newFileName,
        MoveFileFlags flags);
}
