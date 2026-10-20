using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class ThxAudioProvisioningElevationBridge
{
    internal const string Argument = "--request-thx-audio-provisioning";

    internal static bool HasCommandSwitch(IReadOnlyList<string> arguments) =>
        arguments.Any(value => value.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && string.Equals(arguments[0], Argument, StringComparison.OrdinalIgnoreCase);

    internal static int Run()
    {
        bool requested = false;
        try
        {
            string? executable = Environment.ProcessPath;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!WindowsThxAudioProvisioningPlatform.IsInstalledExecutable(executable, programFiles)
                || identity.User is null || identity.IsSystem
                || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                || !ProcessIdToSessionId((uint)Environment.ProcessId, out uint session)
                || session == 0 || session == uint.MaxValue || session != WTSGetActiveConsoleSessionId()
                || !HasTrustedLaunchEnvironment())
            {
                return (int)ThxAudioProvisioningResult.Unavailable;
            }
            ThxAudioProvisioningJournal.RejectReparse(executable!);
            ProcessStartInfo start = CreateStartInfo(executable!);
            requested = true;
            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Windows did not start elevated THX provisioning.");
            process.WaitForExit();
            return Enum.IsDefined(typeof(ThxAudioProvisioningResult), process.ExitCode)
                ? process.ExitCode : (int)ThxAudioProvisioningResult.RecoveryRequired;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return (int)ThxAudioProvisioningResult.Canceled;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            return (int)(requested ? ThxAudioProvisioningResult.RecoveryRequired : ThxAudioProvisioningResult.Failed);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable) => new(executable)
    {
        Arguments = ThxAudioProvisioningCommand.Argument,
        WorkingDirectory = Path.GetDirectoryName(executable)!,
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
    };

    internal static bool HasTrustedLaunchEnvironment()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system32 = Path.Combine(windows, "System32");
        if (!string.Equals(Environment.GetEnvironmentVariable("ComSpec"),
                Path.Combine(system32, "cmd.exe"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Environment.GetEnvironmentVariable("PATH"),
                WindowsThxAudioProvisioningPlatform.GetTrustedSystemPath(windows),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            string name = (string)variable.Key;
            if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WTSGetActiveConsoleSessionId();
}
