using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed class WindowsThxAudioProvisioningPlatform : IThxAudioProvisioningPlatform
{
    public ThxAudioProvisioningSnapshot Capture(ThxAudioProvisioningStep? completedStep = null)
    {
        string? processPath = Environment.ProcessPath;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!IsInstalledExecutable(processPath, programFiles))
        {
            return Refuse(ThxAudioProvisioningResult.Unavailable);
        }
        ThxAudioProvisioningJournal.RejectReparse(processPath!);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!OperatingSystem.IsWindows() || identity.User is null || identity.IsSystem
            || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || !ProcessIdToSessionId((uint)Environment.ProcessId, out uint session)
            || session == 0 || session == uint.MaxValue || session != WTSGetActiveConsoleSessionId())
        {
            return Refuse(ThxAudioProvisioningResult.Unavailable);
        }
        ThxAudioCompatibilityProfile? profile = WindowsThxAudioAdmissionSource.GetExactHostProfile();
        if (profile is null || !WindowsThxAdmissionQueries.HasExactDeviceIdentities(profile))
        {
            return Refuse(ThxAudioProvisioningResult.Unavailable);
        }
        if (profile.ProductId != "rznb11")
        {
            return Refuse(ThxAudioProvisioningResult.SourceUnavailable);
        }
        if (!GetSystemPowerStatus(out PowerStatus power))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        bool acPower = power.AcLineStatus == 1;
        bool controllersAbsent = !WindowsThxAudioAdmissionSource.IsCompetingControllerRunning();

        string driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        bool stackHashes = ThxAudioCompatibility.TryResolveDriverStack(driverStore, profile.Stack,
            out string? serviceBinary);
        bool catalogs = stackHashes && HasTrustedCatalogMembership(driverStore, profile.Stack);
        string propertiesEvidence = GetActiveFileEvidence(commonData, profile,
            $"thx_{profile.ProductId}_props.dat");
        string surroundEvidence = GetActiveFileEvidence(commonData, profile, "surround_714_config.json");
        bool properties = propertiesEvidence == profile.ActivePropertiesSha256;
        bool surround = surroundEvidence == profile.SurroundConfigSha256;
        bool service = stackHashes && catalogs && WindowsThxAdmissionQueries.HasRunningPinnedService(
            profile.Stack.ServiceName, serviceBinary!);
        bool provisioningProcessesAbsent = NoProvisioningProcesses();
        bool complete = stackHashes && catalogs && properties && surround && service;
        ThxAudioProvisioningSnapshot Snapshot(bool ready, bool installed,
            ThxAudioProvisioningResult refusal = ThxAudioProvisioningResult.Unavailable) =>
            new(ready, installed, refusal)
            {
                Profile = profile.ModelNumber + "/" + profile.ProductId,
                ExactHost = true,
                ExactDevices = true,
                AcPower = acPower,
                ControllersAbsent = controllersAbsent,
                StackHashes = stackHashes,
                CatalogMembership = catalogs,
                Service = service,
                ProvisioningProcessesAbsent = provisioningProcessesAbsent,
                ActiveProperties = propertiesEvidence,
                SurroundConfiguration = surroundEvidence,
            };
        if (complete)
        {
            return Snapshot(true, true);
        }
        bool expected = completedStep switch
        {
            // The base installer starts the service, whose initialization may create ProgramData files.
            // No reviewed source pins that intermediate content. The pristine baseline and journal bound
            // its provenance; each updater's immutable output is pinned immediately after its own step.
            ThxAudioProvisioningStep.InstallDriverStack => stackHashes && catalogs && service,
            ThxAudioProvisioningStep.InstallProperties => stackHashes && catalogs && service && properties,
            ThxAudioProvisioningStep.InstallSurroundConfiguration => complete,
            _ => IsPristine(driverStore, commonData),
        };
        if (!expected || !provisioningProcessesAbsent)
        {
            return Snapshot(false, false, ThxAudioProvisioningResult.ExistingInstallation);
        }
        if (!acPower)
        {
            return Snapshot(false, false, ThxAudioProvisioningResult.AcPowerRequired);
        }
        if (!controllersAbsent)
        {
            return Snapshot(false, false, ThxAudioProvisioningResult.ControllerActive);
        }
        return Snapshot(true, false);
    }

    public string ReadBootIdentity()
    {
        using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        query.Options.Timeout = TimeSpan.FromSeconds(2);
        using ManagementObjectCollection results = query.Get();
        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                if (result["LastBootUpTime"] is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime()
                        .ToString("O", CultureInfo.InvariantCulture);
                }
            }
        }
        throw new InvalidOperationException("Windows boot identity is unavailable.");
    }

    public IThxAudioProvisioningJournal OpenJournal() => new ThxAudioProvisioningJournal();
    public Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken) =>
        ThxAudioProvisioningPackage.AcquireVerifiedAsync(destination, cancellationToken);

    public async Task<ThxAudioProvisioningProcessResult> ExecuteAsync(
        string root, ThxAudioProvisioningStep step, CancellationToken cancellationToken)
    {
        ThxAudioProvisioningJournal.ValidateProtectedDirectory(root);
        ThxAudioProvisioningArtifact artifact = ThxAudioProvisioningPackage.Artifacts.Single(item => item.Step == step);
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, artifact.FileName));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("THX provisioning artifact escaped its protected root.");
        }
        ThxAudioProvisioningJournal.RejectReparse(path);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (locked.Length != artifact.Length || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(artifact.Sha256), SHA256.HashData(locked)))
        {
            throw new InvalidDataException("Verified THX provisioning artifact changed before execution.");
        }
        locked.Position = 0;
        ThxNativeComponentTrust.Verify(path, locked.SafeFileHandle,
            ThxAudioProvisioningPackage.SignerSubject,
            ThxAudioProvisioningPackage.SignerThumbprint,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string temp = Path.Combine(fullRoot, "temp");
        ThxAudioProvisioningJournal.EnsureProtectedDirectory(temp);
        ProcessStartInfo start = CreateStartInfo(path, fullRoot, temp, artifact.Arguments);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("THX provisioning process did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        Task<string> output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        Task<string> error = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new(process.ExitCode,
                await output.ConfigureAwait(false) + "\n" + await error.ConfigureAwait(false));
        }
        catch
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    private static bool IsPristine(string driverStore, string commonData)
    {
        try
        {
            if (Directory.EnumerateDirectories(driverStore, "thx_*", SearchOption.TopDirectoryOnly).Any()
                || PathExists(Path.Combine(commonData, "thx")))
            {
                return false;
            }
            using RegistryKey services = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services", writable: false)
                ?? throw new InvalidOperationException("Windows services registry is unavailable.");
            return !services.GetSubKeyNames().Any(name => name.StartsWith("thx_", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsInstalledExecutable(string? processPath, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(programFiles))
        {
            return false;
        }
        string expected = Path.Combine(Path.GetFullPath(programFiles), "OpenBlade", "OpenBlade.SessionAgent.exe");
        return string.Equals(Path.GetFullPath(processPath), expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string root, string temp,
        IReadOnlyList<string> arguments)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system32 = Path.Combine(windows, "System32");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["windir"] = windows;
        start.Environment["SystemDrive"] = Path.GetPathRoot(windows)!;
        start.Environment["ComSpec"] = Path.Combine(system32, "cmd.exe");
        start.Environment["PATH"] = GetTrustedSystemPath(windows);
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        start.Environment["ALLUSERSPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        start.Environment["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        start.Environment["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        start.Environment["OS"] = "Windows_NT";
        start.Environment["PROCESSOR_ARCHITECTURE"] = "AMD64";
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    internal static string GetTrustedSystemPath(string windows)
    {
        string system32 = Path.Combine(windows, "System32");
        return string.Join(Path.PathSeparator, system32, windows, Path.Combine(system32, "Wbem"),
            Path.Combine(system32, "WindowsPowerShell", "v1.0"));
    }

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string GetActiveFileEvidence(string commonData, ThxAudioCompatibilityProfile profile,
        string name) => GetFileEvidence(Path.Combine(commonData, "thx", profile.ProductId, name));

    internal static string GetFileEvidence(string path)
    {
        // A linked updater target must block continuation, even when ordinary
        // intermediate content is not pinned until that updater completes.
        ThxAudioProvisioningJournal.RejectReparse(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024)
            {
                return "oversized";
            }
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (FileNotFoundException) { return "missing"; }
        catch (DirectoryNotFoundException) { return "missing"; }
        catch (UnauthorizedAccessException) { return "unreadable"; }
        catch (IOException) { return "unreadable"; }
    }

    private static bool NoProvisioningProcesses()
    {
        try
        {
            foreach (string processName in ThxAudioProvisioningPackage.Artifacts.Select(
                artifact => Path.GetFileNameWithoutExtension(artifact.FileName)))
            {
                Process[] processes = Process.GetProcessesByName(processName);
                try
                {
                    if (processes.Length != 0)
                    {
                        return false;
                    }
                }
                finally
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasTrustedCatalogMembership(string driverStore, ThxAudioDriverStack stack)
    {
        try
        {
            foreach (ThxAudioDriverPackage package in stack.Packages)
            {
                string[] matchingDirectories = Directory.GetDirectories(driverStore,
                    package.InfName + "_amd64_*", SearchOption.TopDirectoryOnly)
                    .Where(directory => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    .Where(directory => package.Files.All(file => ThxAudioCompatibility.HasHash(
                        Path.Combine(directory, file.Name), file.Sha256)))
                    .ToArray();
                if (matchingDirectories.Length != 1)
                {
                    return false;
                }
                string[] catalogs = package.Files.Where(file => file.Name.EndsWith(".cat", StringComparison.Ordinal))
                    .Select(file => file.Name).ToArray();
                if (catalogs.Length != 1)
                {
                    return false;
                }
                RazerDriverPackageTrust.VerifyCatalogMembers(matchingDirectories[0], catalogs[0],
                    package.Files.Where(file => file.Name != catalogs[0]).Select(file => file.Name),
                    CancellationToken.None, cacheOnly: true);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or Win32Exception)
        {
            return false;
        }
    }

    private static ThxAudioProvisioningSnapshot Refuse(ThxAudioProvisioningResult result) =>
        new(false, false, result);

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            int take = Math.Min(count, 32768 - output.Length);
            if (take > 0)
            {
                output.Append(buffer, 0, take);
            }
        }
        return output.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WTSGetActiveConsoleSessionId();
}
