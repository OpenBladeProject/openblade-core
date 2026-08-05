using System.Globalization;

namespace OpenBlade.Tray;

internal static class McuFirmwareVersionPresentation
{
    public static string Format(string? value)
    {
        string firmwareValue = string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "Unavailable", StringComparison.OrdinalIgnoreCase)
            ? "Unavailable"
            : value.Trim();
        if (Version.TryParse(firmwareValue, out Version? version)
            && version.Build == 0
            && version.Revision == 0
            && version.Minor is >= 0 and <= 99)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{version.Major}.{version.Minor:D2}");
        }

        return firmwareValue;
    }
}
