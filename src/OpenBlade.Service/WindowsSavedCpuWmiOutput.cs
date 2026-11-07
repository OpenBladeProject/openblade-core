using System.Management;
using System.Runtime.InteropServices;

namespace OpenBlade.Service;

internal enum CpuSavedWmiPropertyState { Absent, Present, Error }
internal readonly record struct CpuSavedWmiProperty(CpuSavedWmiPropertyState State, object? Value = null);

// The pinned AMD_ACPI COM provider may omit ReturnValue even though the CIM
// bridge exposes it. This is an explicit compatibility policy, not a general
// WMI convention. Payload validation and complete firmware readback remain
// mandatory, and production CPU mutation admission remains disabled.
internal static class WindowsSavedCpuWmiOutput
{
    internal static CpuSavedWmiProperty Read(ManagementBaseObject? output, string name) => output is null
        ? new(CpuSavedWmiPropertyState.Error)
        : Read(PropertyNames(output),
            property => output[property], name);

    private static IEnumerable<string> PropertyNames(ManagementBaseObject output)
    {
        // Keep even acquisition of Properties inside Read's guarded enumeration.
        foreach (PropertyData property in output.Properties) { yield return property.Name; }
    }

    internal static CpuSavedWmiProperty Read(IEnumerable<string> names, Func<string, object?> read, string name)
    {
        try
        {
            string? found = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in names)
            {
                if (string.IsNullOrEmpty(candidate) || candidate.Length > 128 || seen.Count >= 64 || !seen.Add(candidate))
                { return new(CpuSavedWmiPropertyState.Error); }
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) { found = candidate; }
            }
            // Only a complete successful enumeration establishes absence. A
            // subsequent Get failure for a found property is always an error.
            return found is null ? new(CpuSavedWmiPropertyState.Absent)
                : new(CpuSavedWmiPropertyState.Present, read(found));
        }
        catch (Exception exception) when (exception is ManagementException or COMException
            or InvalidOperationException or ArgumentException or UnauthorizedAccessException
            or OperationCanceledException or System.Security.SecurityException)
        { return new(CpuSavedWmiPropertyState.Error); }
    }

    internal static bool AcceptReturnValue(CpuSavedWmiProperty property, bool pinnedMissingReturnValueCompatibility) =>
        property.State == CpuSavedWmiPropertyState.Present ? property.Value is true
            : property.State == CpuSavedWmiPropertyState.Absent && pinnedMissingReturnValueCompatibility;

    internal static bool AcceptVersion(CpuSavedWmiProperty property) =>
        property is { State: CpuSavedWmiPropertyState.Present, Value: uint value } && value == 7;

    internal static bool AcceptCommandResult(CpuSavedWmiProperty property) =>
        property is { State: CpuSavedWmiPropertyState.Present, Value: byte[] { Length: > 0 and <= 4096 } bytes }
        && bytes[0] == 0;
}
