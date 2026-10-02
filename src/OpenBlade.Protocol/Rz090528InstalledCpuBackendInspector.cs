using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record CpuBackendFileMetadata(string Path, string Sha256, bool SignatureValid, string SignerThumbprint);

internal sealed record InstalledCpuBackendSnapshot(
    IReadOnlyList<CpuBackendFileMetadata> Files,
    bool ServiceExists,
    string? ImagePath,
    int? ServiceType,
    int? Start,
    bool Running);

internal interface IInstalledCpuBackendSnapshotSource
{
    InstalledCpuBackendSnapshot Read();
}

/// <summary>
/// Passive preflight only: never loads AMD DLLs, opens hardware, or changes a
/// service. An available installed stack does not admit native invocation.
/// </summary>
internal sealed class Rz090528InstalledCpuBackendInspector
{
    internal const string DirectoryPath = @"C:\Program Files\Razer\RazerAMDOverClock";
    internal const string DriverPath = DirectoryPath + @"\AMDRyzenMasterDriver.sys";
    internal const string ServiceName = "AMDRyzenMasterDriverV31";
    internal const string SignerThumbprint = "BF2E317BEE75478F8693BA0E29B757AEF5F9A34C";
    internal static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DirectoryPath + @"\Platform.dll"] = "61584FE630DF8E819A324013B3F1C8178364E608B73A13DFF0827743BC26B5DF",
            [DirectoryPath + @"\Device.dll"] = "70B5A3B1B2E6CFA42ECF791A8FE008453F5B5BB521EC9F228DFDBF6739C978D5",
            [DriverPath] = "7250DDCB9B04E7E00694C9D5AE3AA2B37CAAE367A4125BB84D8AC0B0DFAB8624",
        };

    private readonly IInstalledCpuBackendSnapshotSource source;

    internal Rz090528InstalledCpuBackendInspector()
        : this(new WindowsInstalledCpuBackendSnapshotSource()) { }

    internal Rz090528InstalledCpuBackendInspector(IInstalledCpuBackendSnapshotSource source) =>
        this.source = source ?? throw new ArgumentNullException(nameof(source));

    internal CpuTuningBackendStatus Inspect(bool useInstalledBackend, DeviceIdentity identity)
    {
        // The caller must supply freshly verified exact host identity.
        if (!useInstalledBackend || identity is null || identity.Model != "RZ09-0528"
            || identity.VendorId != 0x1532 || identity.ProductId != 0x02C6)
        {
            return Status(CpuTuningBackendAvailability.Unavailable);
        }

        try
        {
            return Evaluate(source.Read());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException
            or System.Security.SecurityException or CryptographicException)
        {
            return Status(CpuTuningBackendAvailability.Failed);
        }
    }

    internal static CpuTuningBackendStatus Evaluate(InstalledCpuBackendSnapshot snapshot)
    {
        if (snapshot.Files.Count < ExpectedHashes.Count || !snapshot.ServiceExists)
        {
            return Status(CpuTuningBackendAvailability.MissingDependency);
        }

        if (snapshot.Files.Count != ExpectedHashes.Count
            || ExpectedHashes.Any(expected => snapshot.Files.Count(file =>
                string.Equals(file.Path, expected.Key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(file.Sha256, expected.Value, StringComparison.OrdinalIgnoreCase)
                && file.SignatureValid
                && string.Equals(file.SignerThumbprint, SignerThumbprint, StringComparison.OrdinalIgnoreCase)) != 1)
            || snapshot.ServiceType != 1
            || !string.Equals(snapshot.ImagePath, @"\??\" + DriverPath, StringComparison.OrdinalIgnoreCase))
        {
            return Status(CpuTuningBackendAvailability.Failed);
        }

        return Status(snapshot.Start is >= 0 and <= 3 && snapshot.Running
            ? CpuTuningBackendAvailability.Available
            : CpuTuningBackendAvailability.Unavailable);
    }

    private static CpuTuningBackendStatus Status(CpuTuningBackendAvailability availability) =>
        new(DeviceFeatureAccess.ObservedOnly, availability);
}

internal sealed class WindowsInstalledCpuBackendSnapshotSource : IInstalledCpuBackendSnapshotSource
{
    internal static IReadOnlyList<CpuBackendFileMetadata> ReadFileMetadata()
    {
        var files = new List<CpuBackendFileMetadata>();
        foreach (string path in Rz090528InstalledCpuBackendInspector.ExpectedHashes.Keys)
        {
            if (!File.Exists(path)) { continue; }
            // Reject directory/file redirection before hashing or signature checks.
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || HasRedirectedParent(path)) { throw new IOException("Installed CPU backend path is redirected."); }
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexString(SHA256.HashData(file));
            (bool valid, string signer) = ReadSignature(path);
            files.Add(new(path, hash, valid, signer));
        }
        return files.AsReadOnly();
    }

    public InstalledCpuBackendSnapshot Read() => Read(ReadFileMetadata());

    // Only the native session's still-held deny-write/delete file pins may
    // supply cached metadata. Service configuration and state remain fresh.
    internal InstalledCpuBackendSnapshot Read(IReadOnlyList<CpuBackendFileMetadata> files)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + Rz090528InstalledCpuBackendInspector.ServiceName,
            writable: false);
        return new(files, key is not null,
            key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            key?.GetValue("Type") as int?, key?.GetValue("Start") as int?,
            key is not null && IsServiceRunning());
    }

    private static bool HasRedirectedParent(string path)
    {
        for (DirectoryInfo? parent = new FileInfo(path).Directory; parent is not null; parent = parent.Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) { return true; }
        }
        return false;
    }

    private static (bool Valid, string Signer) ReadSignature(string path)
    {
        // Same inbox PowerShell signature pattern used by capture's HWiNFO
        // verifier. This project does not reference the Capture executable.
        string inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0");
        var start = new ProcessStartInfo(Path.Combine(inbox, "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PSModulePath"] = Path.Combine(inbox, "Modules");
        start.Environment["OPENBLADE_CPU_SIGNATURE_PATH"] = path;
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath $env:OPENBLADE_CPU_SIGNATURE_PATH; @{Status=$s.Status.ToString();Thumbprint=$s.SignerCertificate.Thumbprint}|ConvertTo-Json -Compress" })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new IOException("Signature verifier unavailable.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Signature verifier timed out.");
        }
        if (process.ExitCode != 0) { throw new IOException("Signature verifier failed."); }
        using JsonDocument document = JsonDocument.Parse(output.GetAwaiter().GetResult());
        _ = error.GetAwaiter().GetResult();
        return (document.RootElement.GetProperty("Status").GetString() == "Valid",
            document.RootElement.GetProperty("Thumbprint").GetString() ?? "");
    }

    private static bool IsServiceRunning()
    {
        // SCM access CONNECT; service access QUERY_STATUS. No start/change/delete.
        nint manager = OpenSCManagerW(null, null, 1);
        if (manager == 0) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            nint service = OpenServiceW(manager, Rz090528InstalledCpuBackendInspector.ServiceName, 4);
            if (service == 0) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                if (!QueryServiceStatusEx(service, 0, out ServiceStatus status,
                    Marshal.SizeOf<ServiceStatus>(), out _))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                return status.ServiceType == 1 && status.CurrentState == 4;
            }
            finally { _ = CloseServiceHandle(service); }
        }
        finally { _ = CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
