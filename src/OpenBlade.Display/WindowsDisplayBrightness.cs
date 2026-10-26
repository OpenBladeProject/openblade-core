using System.Management;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Display;

internal sealed record DisplayBrightnessReading(
    string InstanceName,
    byte Percent,
    IReadOnlyList<byte> Levels);

internal static class WindowsDisplayBrightness
{
    private const string WmiNamespace = @"root\wmi";

    internal static DisplayBrightnessReading? Query(
        string? monitorDevicePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var search = new ManagementObjectSearcher(WmiNamespace,
                "SELECT InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness WHERE Active = TRUE");
            using ManagementObjectCollection results = search.Get();
            var readings = new List<DisplayBrightnessReading>();
            foreach (ManagementBaseObject result in results)
            {
                using (result)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result["InstanceName"] is string instance
                        && result["CurrentBrightness"] is byte percent && percent <= 100
                        && result["Level"] is byte[] levels
                        && MatchesMonitor(monitorDevicePath, instance))
                    {
                        byte[] supported = levels.Where(level => level <= 100).Distinct().Order().ToArray();
                        if (supported.Length > 0)
                        {
                            readings.Add(new(instance, percent, supported));
                        }
                    }
                }
            }
            return readings.Count == 1 ? readings[0] : null;
        }
        catch (Exception exception) when (exception is ManagementException
            or COMException or UnauthorizedAccessException)
        {
            DiagnosticTrace.TraceException(nameof(WindowsDisplayBrightness), nameof(Query), exception);
            return null;
        }
    }

    internal static byte SelectLevel(byte requested, IReadOnlyList<byte> levels)
        => DisplayBrightnessLevels.SelectNearest(requested, levels);

    internal static void Set(
        string monitorDevicePath,
        DisplayBrightnessReading baseline,
        byte target,
        CancellationToken cancellationToken,
        Action onSubmitting)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(target, (byte)100);
        ArgumentNullException.ThrowIfNull(onSubmitting);
        DisplayBrightnessReading fresh = Query(monitorDevicePath, cancellationToken)
            ?? throw new InvalidOperationException("Windows could not confirm the internal display brightness.");
        if (!string.Equals(fresh.InstanceName, baseline.InstanceName, StringComparison.OrdinalIgnoreCase)
            || !fresh.Levels.Contains(target))
        {
            throw new InvalidOperationException("The internal display changed before the brightness operation.");
        }

        using var search = new ManagementObjectSearcher(WmiNamespace,
            "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
        using ManagementObjectCollection results = search.Get();
        var matches = new List<ManagementObject>();
        try
        {
            foreach (ManagementObject method in results.Cast<ManagementObject>())
            {
                bool retained = false;
                try
                {
                    if (string.Equals(method["InstanceName"] as string, fresh.InstanceName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(method);
                        retained = true;
                    }
                }
                finally
                {
                    if (!retained)
                    {
                        method.Dispose();
                    }
                }
            }
            if (matches.Count != 1)
            {
                throw new InvalidOperationException("Windows did not report one brightness controller for the internal display.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            using ManagementBaseObject input = matches[0].GetMethodParameters("WmiSetBrightness");
            input["Timeout"] = 0u;
            input["Brightness"] = target;
            onSubmitting();
            using ManagementBaseObject? output = matches[0].InvokeMethod("WmiSetBrightness", input, null);
            ValidateSetResult(output?["ReturnValue"]);
        }
        finally
        {
            foreach (ManagementObject method in matches)
            {
                method.Dispose();
            }
        }
    }

    internal static void ValidateSetResult(object? returnValue)
    {
        if (returnValue is not uint status)
        {
            string type = returnValue?.GetType().FullName ?? "null";
            throw new ManagementException(
                $"Windows returned an invalid brightness acknowledgement type ({type}).");
        }

        if (status != 0)
        {
            throw new ManagementException(
                $"Windows rejected the brightness change with status {status} (0x{status:X8}).");
        }
    }

    internal static bool MatchesMonitor(string? monitorDevicePath, string instanceName)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath) || string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }
        string path = monitorDevicePath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? monitorDevicePath[4..] : monitorDevicePath;
        string[] parts = path.Split('#');
        if (parts.Length < 3 || !string.Equals(parts[0], "DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string expected = string.Join('\\', parts.Take(3));
        int suffix = instanceName.LastIndexOf('_');
        string instance = suffix > 0 && int.TryParse(instanceName[(suffix + 1)..], out _)
            ? instanceName[..suffix] : instanceName;
        return string.Equals(expected, instance, StringComparison.OrdinalIgnoreCase);
    }
}
