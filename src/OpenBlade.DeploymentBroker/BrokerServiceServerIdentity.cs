using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentBroker;

internal sealed record BrokerServiceConfigurationEvidence(
    bool Exists,
    bool IsRunning,
    uint ServiceType,
    uint StartType,
    string BinaryPath,
    string StartName,
    bool ProgramFilesDirectoryExists,
    bool InstallDirectoryExists,
    bool ExecutableExists,
    bool ProgramFilesDirectoryIsReparsePoint,
    bool InstallDirectoryIsReparsePoint,
    bool ExecutableIsReparsePoint);

internal static class BrokerServiceConfigurationPolicy
{
    internal const uint Win32OwnProcess = 0x00000010;
    internal const uint AutoStart = 0x00000002;

    internal static bool IsTrusted(
        BrokerServiceConfigurationEvidence evidence,
        string expectedExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
        try
        {
            string expected = Path.GetFullPath(expectedExecutablePath);
            return evidence.Exists
                && evidence.IsRunning
                && evidence.ServiceType == Win32OwnProcess
                && evidence.StartType == AutoStart
                && string.Equals(evidence.StartName, "LocalSystem", StringComparison.Ordinal)
                && string.Equals(evidence.BinaryPath, $"\"{expected}\"", StringComparison.OrdinalIgnoreCase)
                && evidence.ProgramFilesDirectoryExists
                && evidence.InstallDirectoryExists
                && evidence.ExecutableExists
                && !evidence.ProgramFilesDirectoryIsReparsePoint
                && !evidence.InstallDirectoryIsReparsePoint
                && !evidence.ExecutableIsReparsePoint;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

internal sealed class WindowsBrokerServiceHealthProbe : IBrokerServiceHealthProbe
{
    internal const string ServiceName = "OpenBlade";

    internal static string ExpectedExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenBlade",
        "OpenBlade.Service.exe");

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            BrokerServiceConfigurationEvidence evidence = WindowsBrokerServiceConfigurationReader.Read();
            return Task.FromResult(BrokerServiceConfigurationPolicy.IsTrusted(
                evidence,
                ExpectedExecutablePath));
        }
        catch (Exception exception) when (exception is
            Win32Exception or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }
}

internal static class WindowsBrokerServiceConfigurationReader
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;
    private const int ErrorInsufficientBuffer = 122;

    internal static BrokerServiceConfigurationEvidence Read()
    {
        using SafeServiceHandle serviceManager = OpenSCManager(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        if (serviceManager.IsInvalid)
        {
            throw NativeFailure("The Windows service manager is unavailable.");
        }

        using SafeServiceHandle service = OpenService(
            serviceManager,
            WindowsBrokerServiceHealthProbe.ServiceName,
            ServiceQueryConfig | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            throw NativeFailure("The OpenBlade service registration is unavailable.");
        }

        uint statusSize = checked((uint)Marshal.SizeOf<ServiceStatusProcess>());
        if (!QueryServiceStatusEx(
            service,
            ScStatusProcessInfo,
            out ServiceStatusProcess status,
            statusSize,
            out _))
        {
            throw NativeFailure("The OpenBlade service status is unavailable.");
        }

        QueryServiceConfig(service, IntPtr.Zero, 0, out uint bytesNeeded);
        int initialError = Marshal.GetLastWin32Error();
        if (bytesNeeded == 0 || initialError != ErrorInsufficientBuffer)
        {
            throw NativeFailure("The OpenBlade service configuration size is unavailable.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
            {
                throw NativeFailure("The OpenBlade service configuration is unavailable.");
            }

            QueryServiceConfigValue config = Marshal.PtrToStructure<QueryServiceConfigValue>(buffer);
            string expected = Path.GetFullPath(WindowsBrokerServiceHealthProbe.ExpectedExecutablePath);
            string installDirectory = Path.GetDirectoryName(expected)
                ?? throw new InvalidDataException(
                    "The protected OpenBlade install directory is unavailable.");
            string programFilesDirectory = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            DirectoryInfo programFiles = new(programFilesDirectory);
            DirectoryInfo install = new(installDirectory);
            FileInfo executable = new(expected);
            return new BrokerServiceConfigurationEvidence(
                Exists: true,
                IsRunning: status.CurrentState == ServiceRunning,
                ServiceType: config.ServiceType,
                StartType: config.StartType,
                BinaryPath: Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty,
                StartName: Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty,
                ProgramFilesDirectoryExists: programFiles.Exists,
                InstallDirectoryExists: install.Exists,
                ExecutableExists: executable.Exists,
                ProgramFilesDirectoryIsReparsePoint: programFiles.Exists
                    && programFiles.Attributes.HasFlag(FileAttributes.ReparsePoint),
                InstallDirectoryIsReparsePoint: install.Exists
                    && install.Attributes.HasFlag(FileAttributes.ReparsePoint),
                ExecutableIsReparsePoint: executable.Exists
                    && executable.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Win32Exception NativeFailure(string message) => new(
        Marshal.GetLastWin32Error(),
        message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigValue
    {
        internal uint ServiceType;
        internal uint StartType;
        internal uint ErrorControl;
        internal IntPtr BinaryPathName;
        internal IntPtr LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies;
        internal IntPtr ServiceStartName;
        internal IntPtr DisplayName;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle service,
        IntPtr serviceConfig,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
