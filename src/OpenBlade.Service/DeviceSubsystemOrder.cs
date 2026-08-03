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

    public static IReadOnlyList<DeviceSubsystem> ForTransition(
        ConfirmedDeviceState initial,
        DesiredDeviceState desired) =>
        IsManualFanMode(initial.Fans)
            && desired.Fans.Value == FanMode.Automatic
                ? FansFirst
                : Default;

    public static bool RequiresManualFanPerformanceBridge(
        ConfirmedDeviceState initial,
        DesiredDeviceState desired) =>
        IsManualFanMode(initial.Fans)
        && IsManualFanMode(desired.Fans.Value);

    public static bool IsThermal(DeviceSubsystem subsystem) => subsystem is
        DeviceSubsystem.Performance or DeviceSubsystem.Fans;

    internal static bool IsManualFanMode(FanMode? mode) => mode is
        FanMode.Maximum or FanMode.Fixed or FanMode.ManualCurve;
}
