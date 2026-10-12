namespace OpenBlade.Display;

internal sealed record DisplayMonitorDeviceIdentity(
    string DeviceInterfaceName,
    string DeviceKey,
    bool IsActive);

internal static class DisplayMonitorDeviceKeyResolver
{
    internal static string Resolve(
        string monitorDevicePath,
        IReadOnlyList<DisplayMonitorDeviceIdentity> monitors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorDevicePath);
        ArgumentNullException.ThrowIfNull(monitors);

        DisplayMonitorDeviceIdentity[] matches = monitors
            .Where(monitor => monitor.IsActive
                && string.Equals(
                    monitor.DeviceInterfaceName,
                    monitorDevicePath,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                matches.Length == 0
                    ? "Windows did not report one active monitor device for the internal display."
                    : "Windows reported more than one active monitor device for the internal display.");
        }

        if (string.IsNullOrWhiteSpace(matches[0].DeviceKey))
        {
            throw new InvalidOperationException(
                "Windows did not report a device key for the active internal display.");
        }

        return matches[0].DeviceKey;
    }
}
