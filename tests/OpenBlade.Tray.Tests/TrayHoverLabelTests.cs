using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class TrayHoverLabelTests
{
    [Theory]
    [InlineData(TrayHoverLabelMode.OpenBlade, "OpenBlade")]
    [InlineData(TrayHoverLabelMode.ActiveProfile, "OpenBlade · Battery · Silent")]
    [InlineData(TrayHoverLabelMode.BatteryPowerFlow, "OpenBlade · Using 18.4 W")]
    [InlineData(
        TrayHoverLabelMode.ProfileAndPowerFlow,
        "OpenBlade · Battery · Silent · Using 18.4 W")]
    public void Format_ProducesEverySupportedHoverLabel(
        TrayHoverLabelMode mode,
        string expected)
    {
        string result = TrayHoverLabelFormatter.Format(
            mode,
            isConnected: true,
            PowerSource.Battery,
            PerformancePreset.Silent,
            "Using 18.4 W");

        Assert.Equal(expected, result);
        Assert.InRange(result.Length, 1, TrayHoverLabelFormatter.MaximumLength);
    }

    [Fact]
    public void Format_DisconnectedAndLongValuesRemainUsefulAndNotifyIconSafe()
    {
        Assert.Equal(
            "OpenBlade · Service unavailable",
            TrayHoverLabelFormatter.Format(
                TrayHoverLabelMode.ProfileAndPowerFlow,
                isConnected: false,
                PowerSource.PluggedIn,
                PerformancePreset.Balanced,
                new string('x', 200)));

        string longResult = TrayHoverLabelFormatter.Format(
            TrayHoverLabelMode.BatteryPowerFlow,
            isConnected: true,
            PowerSource.PluggedIn,
            PerformancePreset.Performance,
            new string('x', 200));
        Assert.Equal(TrayHoverLabelFormatter.MaximumLength, longResult.Length);
    }
}
