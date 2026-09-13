using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal static class DeviceSubsystemFormatter
{
    internal static string FormatList(IEnumerable<DeviceSubsystem> subsystems) =>
        string.Join(
            ", ",
            subsystems.Select(subsystem => subsystem switch
            {
                DeviceSubsystem.Performance => "Power mode",
                DeviceSubsystem.Fans => "Fans",
                DeviceSubsystem.Battery => "Battery",
                DeviceSubsystem.Lighting => "Lighting",
                _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
            }));
}
