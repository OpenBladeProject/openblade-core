using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenBlade.Platform;

public sealed record PawnIoInstallation(Version Version, bool Started, string ContentIdentity);

/// <summary>Read-only admission of the actual PawnIO kernel service and its signed PnP package.</summary>
public static class PawnIoRuntimeAdmission
{
    public static PawnIoInstallation VerifyAvailable(CancellationToken cancellationToken = default)
    {
        PawnIoInstallation state = Inspect(cancellationToken)
            ?? throw new InvalidOperationException("PawnIO is not installed.");
        if (!state.Started)
        {
            throw new InvalidOperationException("The trusted PawnIO device is not started; a restart may be required.");
        }
        return state;
    }

    public static PawnIoInstallation? Inspect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            nint service = OpenService(manager, "PawnIO", 1 | 4); // QUERY_CONFIG | QUERY_STATUS
            if (service == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1060)
                {
                    // A partial vendor installation must not trigger a second installation.
                    using RegistryKey? uninstall = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                    using RegistryKey? devices = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\PawnIO");
                    if (uninstall is not null || devices is not null)
                    { throw new InvalidOperationException("PawnIO registration exists without its kernel service. Preserve it for repair."); }
                    return null;
                }
                throw new Win32Exception(error);
            }
            try
            {
                const int maximumConfigBytes = 16384;
                nint buffer = Marshal.AllocHGlobal(maximumConfigBytes);
                try
                {
                    if (!QueryServiceConfig(service, buffer, maximumConfigBytes, out _))
                    { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    ServiceConfig config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    if (config.ServiceType != 1 || config.StartType != 3)
                    { throw new InvalidDataException("PawnIO is not the expected demand-start kernel service."); }
                    string driver = NormalizeDriverPath(Marshal.PtrToStringUni(config.BinaryPath) ?? string.Empty,
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(driver);
                    Version version = new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                    // Version is provenance. The reviewed API/module handshake and a
                    // complete temperature read establish runtime compatibility; the
                    // bundled installer's version is not a runtime build allowlist.
                    string catalog = Path.Combine(Path.GetDirectoryName(driver)!, "PawnIO.cat");
                    string identity = WindowsDriverApiTrust.VerifyCatalogMembers(catalog, [driver],
                        [WindowsDriverApiTrust.MicrosoftHardwareCompatibilityPublisher], cancellationToken);
                    InspectDevice(cancellationToken);
                    if (!QueryServiceStatus(service, out ServiceStatus status))
                    { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    // The official 2.1 INF has the same root binding but its driver
                    // predates full PnP support (upstream b312f788). Require the
                    // actual kernel service to be running, without treating an old
                    // devnode's DN_STARTED flag as an API compatibility selector.
                    bool started = status.CurrentState == 4;
                    return new(version, started, identity);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static string NormalizeDriverPath(string imagePath, string windows)
    {
        string path = imagePath;
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        { path = Path.Combine(windows, path[12..]); }
        else if (path.StartsWith(@"\??\", StringComparison.Ordinal)) { path = path[4..]; }
        string root = Path.Combine(windows, "System32", "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path), "PawnIO.sys", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("PawnIO must bind the signed package in the Windows DriverStore."); }
        string package = Path.GetDirectoryName(path)!;
        if (!string.Equals(Path.GetDirectoryName(package) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(package).StartsWith("pawnio.inf_amd64_", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("PawnIO has an unexpected driver package identity."); }
        return path;
    }

    private static void InspectDevice(CancellationToken cancellationToken)
    {
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\PawnIO");
        string[] instances = root?.GetSubKeyNames() ?? [];
        if (instances.Length != 1)
        { throw new InvalidDataException("PawnIO must have one independently present Root\\PawnIO device."); }
        cancellationToken.ThrowIfCancellationRequested();
        using RegistryKey instance = root!.OpenSubKey(instances[0])
            ?? throw new InvalidDataException("The PawnIO device registration disappeared.");
        if (!string.Equals(instance.GetValue("Service") as string, "PawnIO", StringComparison.OrdinalIgnoreCase)
            || instance.GetValue("HardwareID") is not string[] ids
            || !ids.Contains(@"Root\PawnIO", StringComparer.OrdinalIgnoreCase))
        { throw new InvalidDataException("The PawnIO device is not bound to its reviewed service and hardware ID."); }
        uint result = CM_Locate_DevNode(out uint devnode, @"ROOT\PawnIO\" + instances[0], 0);
        if (result != 0) { throw new InvalidDataException($"PawnIO device is not present (ConfigManager {result})."); }
        result = CM_Get_DevNode_Status(out _, out _, devnode, 0);
        if (result != 0) { throw new InvalidDataException($"PawnIO device status is unavailable (ConfigManager {result})."); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public nint BinaryPath, LoadOrderGroup;
        public uint TagId;
        public nint Dependencies, ServiceStartName, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(nint service, nint config, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint devnode, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devnode, uint flags);
}
