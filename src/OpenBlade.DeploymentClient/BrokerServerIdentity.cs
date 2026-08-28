using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient;

internal sealed record BrokerServerIdentity(
    uint PipeServerProcessId,
    uint ServiceProcessId,
    bool ServiceIsRunning,
    string ServiceName,
    uint ServiceType,
    uint StartType,
    string BinaryPath,
    string StartName);

internal interface IBrokerServerIdentityVerifier
{
    void Verify(BrokerServerIdentity identity);
}

internal static class BrokerServerIdentityPolicy
{
    internal static bool IsTrusted(
        BrokerServerIdentity identity,
        string expectedServiceName,
        string expectedExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
        try
        {
            string expectedCommandLine = $"\"{Path.GetFullPath(expectedExecutablePath)}\"";
            return identity.PipeServerProcessId != 0
                && identity.PipeServerProcessId == identity.ServiceProcessId
                && identity.ServiceIsRunning
                && string.Equals(
                    identity.ServiceName,
                    expectedServiceName,
                    StringComparison.Ordinal)
                && identity.ServiceType == ServiceWin32OwnProcess
                && identity.StartType == ServiceDemandStart
                && string.Equals(
                    identity.BinaryPath,
                    expectedCommandLine,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(identity.StartName, "LocalSystem", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceDemandStart = 0x00000003;
}

internal sealed class WindowsProtectedBrokerIdentityVerifier : IBrokerServerIdentityVerifier
{
    internal const string ServiceName = "OpenBlade Local Dev Deployment Broker";

    internal static string ExpectedExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenBlade Local Dev Deployment Broker",
        "OpenBlade.DeploymentBroker.exe");

    public void Verify(BrokerServerIdentity identity)
    {
        if (!BrokerServerIdentityPolicy.IsTrusted(
                identity,
                ServiceName,
                ExpectedExecutablePath))
        {
            throw new UnauthorizedAccessException(
                "The connected pipe server is not the exact running OpenBlade deployment broker service.");
        }
    }
}

internal static class WindowsBrokerServerIdentityReader
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;
    private const int ErrorInsufficientBuffer = 122;

    internal static BrokerServerIdentity Read(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected
            || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pipeProcessId)
            || pipeProcessId == 0)
        {
            throw IdentityUnavailable("The deployment broker pipe server PID is unavailable.");
        }

        using SafeServiceHandle serviceManager = OpenSCManager(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        if (serviceManager.IsInvalid)
        {
            throw IdentityUnavailable(
                "The Windows Service Control Manager is unavailable for broker verification.");
        }

        using SafeServiceHandle service = OpenService(
            serviceManager,
            WindowsProtectedBrokerIdentityVerifier.ServiceName,
            ServiceQueryConfig | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            throw IdentityUnavailable(
                "The OpenBlade deployment broker service is unavailable for verification.");
        }

        uint statusSize = checked((uint)Marshal.SizeOf<ServiceStatusProcess>());
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out ServiceStatusProcess serviceStatus,
                statusSize,
                out _))
        {
            throw IdentityUnavailable(
                "The OpenBlade deployment broker service process identity is unavailable.");
        }

        if (serviceStatus.ProcessId == 0)
        {
            throw new UnauthorizedAccessException(
                "The OpenBlade deployment broker service reported an invalid process identity.");
        }

        QueryServiceConfig(service, IntPtr.Zero, 0, out uint bytesNeeded);
        int sizeError = Marshal.GetLastWin32Error();
        if (bytesNeeded == 0 || sizeError != ErrorInsufficientBuffer)
        {
            throw IdentityUnavailable(
                "The OpenBlade deployment broker service configuration size is unavailable.",
                sizeError);
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
            {
                throw IdentityUnavailable(
                    "The OpenBlade deployment broker service configuration is unavailable.");
            }

            ServiceConfiguration configuration = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            string binaryPath = Marshal.PtrToStringUni(configuration.BinaryPathName)
                ?? throw new UnauthorizedAccessException(
                    "The OpenBlade deployment broker service binary path is unavailable.");
            string startName = Marshal.PtrToStringUni(configuration.ServiceStartName)
                ?? throw new UnauthorizedAccessException(
                    "The OpenBlade deployment broker service account is unavailable.");
            return new BrokerServerIdentity(
                pipeProcessId,
                serviceStatus.ProcessId,
                serviceStatus.CurrentState == ServiceRunning,
                WindowsProtectedBrokerIdentityVerifier.ServiceName,
                configuration.ServiceType,
                configuration.StartType,
                binaryPath,
                startName);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static UnauthorizedAccessException IdentityUnavailable(
        string message,
        int? error = null) => new(
            message,
            new Win32Exception(error ?? Marshal.GetLastWin32Error()));

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
    private struct ServiceConfiguration
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle service,
        IntPtr serviceConfig,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
