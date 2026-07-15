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

public sealed class WindowsHardwareAccessGuard(
    IRunningProcessSource processSource,
    IWindowsServiceSource serviceSource) : IHardwareAccessGuard
{
    private static readonly HashSet<string> KnownControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameManagerService3",
        "razer_elevation_service",
        "RazerAppEngine",
        "razerwdl",
        "Razer Chroma SDK Diagnostic Service",
        "Razer Chroma SDK Server",
        "Razer Chroma SDK Service",
        "Razer Chroma Stream Server",
        "Razer Elevation Service",
        "Razer Game Manager Service 3",
        "RGS",
        "Razer Gaming Services Updater",
        "Razer Synapse Service",
    };

    public HardwareAccessDecision Check()
    {
        List<string> conflicts = [];
        bool processFailed = false;
        bool serviceFailed = false;

        try
        {
            conflicts.AddRange(processSource
                .GetProcessNames()
                .Where(KnownControllers.Contains));
        }
        catch (Exception)
        {
            processFailed = true;
        }

        try
        {
            conflicts.AddRange(serviceSource
                .GetServices()
                .Where(service => IsActive(service.State))
                .Where(service =>
                    KnownControllers.Contains(service.ServiceName)
                    || KnownControllers.Contains(service.DisplayName))
                .Select(service => KnownControllers.Contains(service.DisplayName)
                    ? service.DisplayName
                    : service.ServiceName));
        }
        catch (Exception)
        {
            serviceFailed = true;
        }

        string[] confirmed = conflicts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(conflict => conflict, StringComparer.Ordinal)
            .ToArray();
        string? detectionFailure = (processFailed, serviceFailed) switch
        {
            (true, true) => "Process and Windows service enumeration failed.",
            (true, false) => "Process enumeration failed.",
            (false, true) => "Windows service enumeration failed.",
            _ => null,
        };

        return new HardwareAccessDecision(confirmed, detectionFailure);
    }

    private static bool IsActive(WindowsServiceState state) =>
        state is WindowsServiceState.Running
            or WindowsServiceState.StartPending
            or WindowsServiceState.ContinuePending;
}
