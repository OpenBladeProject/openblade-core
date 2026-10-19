using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace OpenBlade.Tray;

internal sealed class ThxAudioInstallLauncher
{
    internal const string Argument = "--request-thx-audio-provisioning";
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync;

    internal ThxAudioInstallLauncher() : this(RunProcessAsync) { }
    internal ThxAudioInstallLauncher(Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync) =>
        this.runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));

    // Presentation only. The elevated helper independently repeats exact host,
    // USB, codec, controller, power and installation-state admission.
    internal static bool IsSupportedHost()
    {
        try
        {
            using RegistryKey registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? bios = registry.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            using RegistryKey? system = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SystemInformation");
            return MatchesHost(bios?.GetValue("SystemManufacturer") as string,
                bios?.GetValue("SystemProductName") as string,
                system?.GetValue("SystemManufacturer") as string,
                system?.GetValue("SystemProductName") as string);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool MatchesHost(string? biosManufacturer, string? biosProduct,
        string? systemManufacturer, string? systemProduct) =>
        biosManufacturer == "Razer" && systemManufacturer == "Razer" && biosProduct == systemProduct
        && biosProduct is "Blade 16 - RZ09-0528" or "Blade 14 - RZ09-0530";

    internal static ProcessStartInfo CreateStartInfo()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenBlade");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system32 = Path.Combine(windows, "System32");
        var start = new ProcessStartInfo(Path.Combine(root, "OpenBlade.SessionAgent.exe"))
        {
            Arguments = Argument,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        // The fixed normal-user bridge requests UAC only after starting with this
        // clean environment. The elevated managed helper inherits no startup hooks
        // or profiler paths; vendor children get their own protected temp directory.
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["windir"] = windows;
        start.Environment["SystemDrive"] = Path.GetPathRoot(windows)!;
        start.Environment["ComSpec"] = Path.Combine(system32, "cmd.exe");
        start.Environment["PATH"] = string.Join(Path.PathSeparator, system32, windows,
            Path.Combine(system32, "Wbem"), Path.Combine(system32, "WindowsPowerShell", "v1.0"));
        start.Environment["TEMP"] = Path.Combine(windows, "Temp");
        start.Environment["TMP"] = Path.Combine(windows, "Temp");
        start.Environment["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        start.Environment["ALLUSERSPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        start.Environment["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        start.Environment["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        start.Environment["OS"] = "Windows_NT";
        start.Environment["PROCESSOR_ARCHITECTURE"] = "AMD64";
        return start;
    }

    internal async Task<ThxAudioProvisioningResult> RequestAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ThxAudioProvisioningResult.Canceled;
        }
        bool started = false;
        try
        {
            ProcessStartInfo start = CreateStartInfo();
            started = true;
            int exitCode = await runAsync(start, cancellationToken).ConfigureAwait(false);
            return Enum.IsDefined(typeof(ThxAudioProvisioningResult), exitCode)
                ? (ThxAudioProvisioningResult)exitCode : ThxAudioProvisioningResult.RecoveryRequired;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return ThxAudioProvisioningResult.Canceled;
        }
        catch (OperationCanceledException)
        {
            return started ? ThxAudioProvisioningResult.RecoveryRequired : ThxAudioProvisioningResult.Canceled;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return started ? ThxAudioProvisioningResult.RecoveryRequired : ThxAudioProvisioningResult.Failed;
        }
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows could not start THX setup.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
