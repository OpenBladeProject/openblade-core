using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal static class DeviceSubsystemOrder
{
    public static IReadOnlyList<DeviceSubsystem> Default { get; } =
    [
        DeviceSubsystem.Performance,
        DeviceSubsystem.Fans,
        DeviceSubsystem.Battery,
        DeviceSubsystem.Lighting,
    ];

    public static IReadOnlyList<DeviceSubsystem> FansFirst { get; } =
    [
        DeviceSubsystem.Fans,
        DeviceSubsystem.Performance,
        DeviceSubsystem.Battery,
        DeviceSubsystem.Lighting,
    ];
}
