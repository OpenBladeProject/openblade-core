using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Setup;

internal static class WindowsSetupCandidateService
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 4;
    private const int ErrorInsufficientBuffer = 122;

    // SCM queries are synchronous. Each query owns its handles so the waiting
    // probe can honor its deadline without sharing resources with a pending read.
    internal static Task<SetupCandidateServiceObservation> ReadAsync(CancellationToken cancellationToken)
        => Task.Run(() => Read(cancellationToken), cancellationToken);

    private static SetupCandidateServiceObservation Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SafeServiceHandle manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw Failure("The Windows Service Control Manager is unavailable for candidate verification.");
        }

        using SafeServiceHandle service = OpenService(manager, OpenBladeWindowsService.Name, ServiceQueryConfig | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            throw Failure("The fixed OpenBlade service is unavailable for candidate verification.");
        }

        QueryServiceConfig(service, IntPtr.Zero, 0, out uint bytesNeeded);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || bytesNeeded == 0)
        {
            throw Failure("The candidate service configuration size is unavailable.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
            {
                throw Failure("The candidate service configuration is unavailable.");
            }

            if (!QueryServiceStatusEx(service, 0, out ServiceStatus status, Marshal.SizeOf<ServiceStatus>(), out _))
            {
                throw Failure("The candidate service process identity is unavailable.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            return new(status.ProcessId, status.CurrentState == ServiceRunning && status.ServiceType == config.ServiceType,
                config.ServiceType, config.StartType, Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty,
                Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Win32Exception Failure(string message) => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode;
        public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    private sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(SafeServiceHandle service, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int level, out ServiceStatus status,
        int size, out uint needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
