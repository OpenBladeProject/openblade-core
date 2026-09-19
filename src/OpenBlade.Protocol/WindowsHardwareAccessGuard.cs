using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace OpenBlade.Protocol;

public interface IRunningProcessSource
{
    IReadOnlyList<string> GetProcessNames();
}

public interface IWindowsServiceSource
{
    IReadOnlyList<WindowsServiceSnapshot> GetServices();
}

public enum WindowsServiceState
{
    Other,
    Stopped,
    Running,
    StartPending,
    ContinuePending,
}

public sealed record WindowsServiceSnapshot(
    string ServiceName,
    string DisplayName,
    WindowsServiceState State);

public sealed class RunningProcessSource : IRunningProcessSource
{
    public IReadOnlyList<string> GetProcessNames()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            var names = new List<string>(processes.Length);
            foreach (Process process in processes)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (Exception exception) when (exception is
                    InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Processes can exit or become inaccessible between the
                    // snapshot and this read. Keep the rest of the observation.
                }
            }

            return names;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal IReadOnlyList<RunningControllerProcess> GetControllerProcessSnapshots()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            List<RunningControllerProcess> snapshots = [];
            foreach (Process process in processes)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (Exception exception) when (exception is
                    InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    continue;
                }

                string? image = null;
                DateTimeOffset? startedAt = null;
                if (name.Equals("RazerAppEngine", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        image = process.MainModule?.FileName;
                        startedAt = process.StartTime.ToUniversalTime();
                    }
                    catch (Exception exception) when (exception is
                        InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // An inaccessible engine must retain its controller conflict.
                    }
                }

                snapshots.Add(new RunningControllerProcess(name, image, startedAt));
            }

            return snapshots;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}

public sealed class WindowsServiceSource : IWindowsServiceSource
{
    public IReadOnlyList<WindowsServiceSnapshot> GetServices()
    {
        ServiceController[] controllers = ServiceController.GetServices();
        try
        {
            var services = new List<WindowsServiceSnapshot>(controllers.Length);
            foreach (ServiceController controller in controllers)
            {
                try
                {
                    services.Add(new WindowsServiceSnapshot(
                        controller.ServiceName,
                        controller.DisplayName,
                        Map(controller.Status)));
                }
                catch (Exception exception) when (exception is
                    InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Services can disappear while the SCM snapshot is read.
                    // One stale entry must not invalidate the full observation.
                }
            }

            return services;
        }
        finally
        {
            foreach (ServiceController controller in controllers)
            {
                controller.Dispose();
            }
        }
    }

    private static WindowsServiceState Map(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => WindowsServiceState.Stopped,
        ServiceControllerStatus.Running => WindowsServiceState.Running,
        ServiceControllerStatus.StartPending => WindowsServiceState.StartPending,
        ServiceControllerStatus.ContinuePending => WindowsServiceState.ContinuePending,
        _ => WindowsServiceState.Other,
    };
}

public sealed class WindowsHardwareAccessGuard : IHardwareAccessGuard
{
    private readonly IControllerPresenceMonitor monitor;

    public WindowsHardwareAccessGuard(
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource)
        : this(new WindowsControllerPresenceMonitor(processSource, serviceSource))
    {
    }

    public WindowsHardwareAccessGuard(IControllerPresenceMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        this.monitor = monitor;
    }

    public HardwareAccessDecision Check()
    {
        ControllerPresenceObservation observation = monitor.Observe();
        return new HardwareAccessDecision(
            observation.PotentialControllers,
            observation.DetectionWarning);
    }
}
