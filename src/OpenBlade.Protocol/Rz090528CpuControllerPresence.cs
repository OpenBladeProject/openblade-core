using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class Rz090528CpuControllerPresence
{
    internal static bool IsGetterControllerName(string name) => IsControllerName(name)
        || RazerControllerServiceAliases.Contains(name);

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
            if (!ServicesAbsent(inventory)) { return false; }
            // Detect a controller started while the full service snapshot ran.
            return NoProcesses(processes());
        }
        catch { return false; }
    }

    private static bool ServicesAbsent(IReadOnlyList<CpuServiceInventoryEntry> inventory, Action<string>? rejection = null)
    {
        bool Reject(string reason) { ReportRejection(rejection, reason); return false; }
        if (inventory.Count > CpuServiceInventory.MaximumEntries) { return Reject("service inventory unavailable"); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in inventory)
        {
            if (string.IsNullOrWhiteSpace(service.Name) || service.Name.Length > 256
                || string.IsNullOrWhiteSpace(service.DisplayName) || service.DisplayName.Length > 256
                || !names.Add(service.Name) || !CpuServiceInventory.TypesAgree(CpuServiceInventoryKind.Win32, service.ConfiguredType, service.Type)
                || service.State is < 1 or > 7) { return Reject("service inventory unavailable"); }
            if ((IsControllerName(service.Name) || IsControllerName(service.DisplayName)) && service.State != 1)
            { return Reject("controller service active"); }
        }
        return true;
    }

    internal static Func<bool>? CaptureGetterContinuity(Action<string>? rejection = null)
    {
        var inventory = new CpuServiceInventory(CpuServiceInventoryKind.Win32);
        return CaptureGetterContinuity(ReadProcessNames, inventory.ReadAll, inventory.ReadGetterActivity, rejection);
    }

    internal static Func<bool>? CaptureGetterContinuity(Func<IReadOnlyList<string>> processes,
        Func<IReadOnlyList<CpuServiceInventoryEntry>> full,
        Func<IReadOnlyList<CpuServiceInventoryEntry>, IReadOnlyList<CpuServiceInventoryEntry>> activity,
        Action<string>? rejection = null)
    {
        try
        {
            var baseline = full();
            if (!GetterAbsent(processes, () => baseline, rejection)) { return null; }
            int revoked = 0;
            bool Current()
            {
                if (Volatile.Read(ref revoked) != 0) { return false; }
                bool allowed = GetterAbsent(processes, () => activity(baseline), rejection);
                if (!allowed) { Interlocked.Exchange(ref revoked, 1); }
                return allowed;
            }
            return Current;
        }
        catch { ReportRejection(rejection, "service inventory unavailable"); return null; }
    }

    private static bool GetterAbsent(Func<IReadOnlyList<string>> processes,
        Func<IReadOnlyList<CpuServiceInventoryEntry>> services, Action<string>? rejection)
    {
        bool Reject(string reason) { ReportRejection(rejection, reason); return false; }
        bool AliasAbsent()
        {
            try
            {
                var names = processes();
                if (names.Count > 16384) { return Reject("process inventory invalid"); }
                foreach (var name in names)
                {
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 1024) { return Reject("process inventory invalid"); }
                    if (IsControllerName(name)) { return Reject("controller process present"); }
                }
                return names.All(name => !RazerControllerServiceAliases.Contains(name)) || Reject("controller process present");
            }
            catch { return Reject("process inventory unavailable"); }
        }
        try
        {
            if (!AliasAbsent()) { return false; }
            var snapshot = services();
            return ServicesAbsent(snapshot, rejection)
                && (snapshot.All(service => service.State == 1
                    || !RazerControllerServiceAliases.MatchesService(service.Name, service.DisplayName))
                    || Reject("controller service active"))
                && AliasAbsent();
        }
        catch (IOException exception) when (exception.Message is "Getter service disappeared." or "Getter template changed."
            or "Getter service identity changed." or "Getter controller configuration changed." or "Getter service inventory changed.")
        { return Reject(exception.Message); }
        catch { return Reject("service inventory unavailable"); }
    }

    private static void ReportRejection(Action<string>? rejection, string reason)
    {
        try { rejection?.Invoke(reason); }
        catch { } // Diagnostics cannot change admission or prevent permanent revocation.
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
