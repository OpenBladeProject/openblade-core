using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class TrayIconStateResolverTests
{
    public static TheoryData<PowerSource, PerformancePreset, TrayIconState> ConfirmedStates =>
        new()
        {
            { PowerSource.PluggedIn, PerformancePreset.Balanced, TrayIconState.AcBalanced },
            { PowerSource.PluggedIn, PerformancePreset.Silent, TrayIconState.AcSilent },
            { PowerSource.PluggedIn, PerformancePreset.Performance, TrayIconState.AcPerformance },
            { PowerSource.UsbC, PerformancePreset.Balanced, TrayIconState.UsbCBalanced },
            { PowerSource.Battery, PerformancePreset.Balanced, TrayIconState.BatteryBalanced },
            { PowerSource.Battery, PerformancePreset.BatteryBalanced, TrayIconState.BatteryBalanced },
            { PowerSource.Battery, PerformancePreset.BatterySaver, TrayIconState.BatterySaver },
        };

    [Theory]
    [MemberData(nameof(ConfirmedStates))]
    public void ConfirmedStateSelectsMatchingTrayIcon(
        PowerSource source,
        PerformancePreset performance,
        TrayIconState expected)
    {
        TrayIconState actual = TrayIconStateResolver.Resolve(
            isConnected: true,
            deviceVerified: true,
            source,
            performance);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, true, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, false, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.ExternalUnknown, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.PluggedIn, null)]
    [InlineData(true, true, PowerSource.UsbC, PerformancePreset.Performance)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Performance)]
    public void UnavailableOrInconsistentStateUsesGenericError(
        bool isConnected,
        bool deviceVerified,
        PowerSource source,
        PerformancePreset? performance)
    {
        TrayIconState actual = TrayIconStateResolver.Resolve(
            isConnected,
            deviceVerified,
            source,
            performance);

        Assert.Equal(TrayIconState.GenericError, actual);
    }
}
