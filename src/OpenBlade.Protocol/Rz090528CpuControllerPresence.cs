using System.Diagnostics;
using System.ServiceProcess;

namespace OpenBlade.Protocol;

internal static class Rz090528CpuControllerPresence
{
    internal static bool IsControllerName(string name) =>
        name.Contains("Razer", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);

    internal static bool ControllersAbsent()
    {
        // The HID-only Synapse blocker does not establish CPU isolation.
        // GetServices enumerates user-mode services, excluding kernel drivers.
        var processes = Process.GetProcesses();
        try
        {
            if (processes.Any(process => IsControllerName(process.ProcessName))) { return false; }
        }
        finally { foreach (var process in processes) { process.Dispose(); } }
        var services = ServiceController.GetServices();
        try
        {
            return !services.Any(service =>
                (IsControllerName(service.ServiceName) || IsControllerName(service.DisplayName))
                && service.Status != ServiceControllerStatus.Stopped);
        }
        finally { foreach (var service in services) { service.Dispose(); } }
    }
}
