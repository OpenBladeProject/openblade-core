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
            return processes.Select(process => process.ProcessName).ToArray();
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
            return controllers
                .Select(controller => new WindowsServiceSnapshot(
                    controller.ServiceName,
                    controller.DisplayName,
                    Map(controller.Status)))
                .ToArray();
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
