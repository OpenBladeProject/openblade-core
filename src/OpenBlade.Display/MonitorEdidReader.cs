using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Display;

internal static class MonitorEdidReader
{
    internal static bool TryReadVerticalRange(string monitorDevicePath, out EdidVerticalRange range)
    {
        range = default;
        return TryRead(monitorDevicePath, out byte[] edid) &&
            EdidVerticalRangeParser.TryParse(edid, out range);
    }

    internal static bool TryRead(string monitorDevicePath, out byte[] edid)
    {
        edid = [];
        if (!TryGetRegistrySubKey(monitorDevicePath, out string subKey))
        {
            return false;
        }

        try
        {
            using RegistryKey? parameters = Registry.LocalMachine.OpenSubKey(subKey);
            if (parameters?.GetValue("EDID") is not byte[] value)
            {
                return false;
            }

            edid = value;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticTrace.TraceException(nameof(MonitorEdidReader), nameof(TryRead), exception);
            return false;
        }
    }

    internal static bool TryGetRegistrySubKey(string monitorDevicePath, out string subKey)
    {
        subKey = string.Empty;
        if (string.IsNullOrWhiteSpace(monitorDevicePath))
        {
            return false;
        }

        string[] segments = monitorDevicePath.Split('#');
        if (segments.Length < 3 ||
            !segments[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ||
            !IsSafeSegment(segments[1]) ||
            !IsSafeSegment(segments[2]))
        {
            return false;
        }

        subKey = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{segments[1]}\{segments[2]}\Device Parameters";
        return true;
    }

    private static bool IsSafeSegment(string value) =>
        value.Length > 0 && value.IndexOfAny(['\\', '/']) < 0;
}
