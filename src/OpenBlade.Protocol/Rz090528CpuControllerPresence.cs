using System.Diagnostics;

namespace OpenBlade.Protocol;

internal static class Rz090528CpuControllerPresence
{
    internal static bool IsControllerName(string name) =>
        name.Contains("Razer", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);

    internal static bool ControllersAbsent() => ControllersAbsent(ReadProcessNames,
        () => new CpuServiceInventory(CpuServiceInventoryKind.Win32).ReadAll());

    internal static bool ControllersAbsent(Func<IReadOnlyList<string>> processes,
        Func<IReadOnlyList<CpuServiceInventoryEntry>> services)
    {
        try
        {
            if (!NoProcesses(processes())) { return false; }
            var inventory = services();
            if (inventory.Count > CpuServiceInventory.MaximumEntries) { return false; }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var service in inventory)
            {
                if (string.IsNullOrWhiteSpace(service.Name) || service.Name.Length > 256
                    || string.IsNullOrWhiteSpace(service.DisplayName) || service.DisplayName.Length > 256
                    || !names.Add(service.Name) || !CpuServiceInventory.TypesAgree(CpuServiceInventoryKind.Win32, service.ConfiguredType, service.Type)
                    || service.State is < 1 or > 7) { return false; }
                if ((IsControllerName(service.Name) || IsControllerName(service.DisplayName)) && service.State != 1) { return false; }
            }
            // Detect a controller started while the full service snapshot ran.
            return NoProcesses(processes());
        }
        catch { return false; }
    }
    private static bool NoProcesses(IReadOnlyList<string> names) => names.Count <= 16384
        && names.All(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 1024 && !IsControllerName(name));
    private static IReadOnlyList<string> ReadProcessNames()
    {
        var processes = Process.GetProcesses();
        try
        {
            if (processes.Length > 16384) { throw new IOException("Process inventory bound."); }
            return processes.Select(process => process.ProcessName).ToArray();
        }
        finally
        {
            bool failed = false;
            foreach (var process in processes) { try { process.Dispose(); } catch { failed = true; } }
            if (failed) { throw new IOException("Process inventory cleanup failed."); }
        }
    }
}
