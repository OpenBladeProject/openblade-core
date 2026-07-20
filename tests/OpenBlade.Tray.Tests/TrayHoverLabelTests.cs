using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class TrayHoverLabelTests
{
    [Theory]
    [InlineData(TrayHoverLabelMode.OpenBlade, "OpenBlade")]
    [InlineData(TrayHoverLabelMode.ActiveProfile, "OpenBlade · Battery · Battery Saver")]
    [InlineData(TrayHoverLabelMode.BatteryPowerFlow, "OpenBlade · Using 18.4 W")]
    [InlineData(
        TrayHoverLabelMode.ProfileAndPowerFlow,
        "OpenBlade · Battery · Battery Saver · Using 18.4 W")]
    public void Format_ProducesEverySupportedHoverLabel(
        TrayHoverLabelMode mode,
        string expected)
    {
        string result = TrayHoverLabelFormatter.Format(
            mode,
            isConnected: true,
            PowerSource.Battery,
            PerformancePreset.BatterySaver,
            "Using 18.4 W");

        Assert.Equal(expected, result);
        Assert.InRange(result.Length, 1, TrayHoverLabelFormatter.MaximumLength);
    }

    [Fact]
    public void Format_PreservesSilentDiagnosticLabelWhenPluggedIn()
    {
        Assert.Equal(
            "OpenBlade · AC · Silent",
            TrayHoverLabelFormatter.Format(
                TrayHoverLabelMode.ActiveProfile,
                isConnected: true,
                PowerSource.PluggedIn,
                PerformancePreset.Silent,
                "Not using battery"));
    }

    [Theory]
    [InlineData(PowerSource.UsbC, PerformancePreset.Balanced, "OpenBlade · USB-C · Balanced")]
    [InlineData(PowerSource.ExternalUnknown, PerformancePreset.Balanced, "OpenBlade · Checking power source")]
    public void Format_DistinguishesUsbCFromUnresolvedExternalPower(
        PowerSource source,
        PerformancePreset performance,
        string expected)
    {
        Assert.Equal(
            expected,
            TrayHoverLabelFormatter.Format(
                TrayHoverLabelMode.ActiveProfile,
                isConnected: true,
                source,
                performance,
                "Battery power unavailable"));
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

    [Fact]
    public void Format_UsesEnabledItemsInUserSelectedOrder()
    {
        string result = TrayHoverLabelFormatter.Format(
            [
                TrayHoverLabelItemKind.GpuTemperature,
                TrayHoverLabelItemKind.PowerSource,
                TrayHoverLabelItemKind.PowerMode,
                TrayHoverLabelItemKind.CpuTemperature,
            ],
            isConnected: true,
            PowerSource.UsbC,
            PerformancePreset.Balanced,
            "Not charging",
            cpuTemperatureCelsius: 47,
            gpuTemperatureCelsius: 52,
            fan1Rpm: 2_300,
            fan2Rpm: 2_500);

        Assert.Equal("OpenBlade · GPU 52 °C · USB-C · Balanced · CPU 47 °C", result);
        Assert.InRange(result.Length, 1, TrayHoverLabelFormatter.MaximumLength);
    }

    [Fact]
    public void Format_OmitsItemsThatWouldOverflowWithoutReorderingThem()
    {
        string result = TrayHoverLabelFormatter.Format(
            [
                TrayHoverLabelItemKind.PowerSource,
                TrayHoverLabelItemKind.PowerMode,
                TrayHoverLabelItemKind.Fan1Speed,
                TrayHoverLabelItemKind.Fan2Speed,
                TrayHoverLabelItemKind.BatteryPower,
            ],
            isConnected: true,
            PowerSource.Battery,
            PerformancePreset.BatterySaver,
            new string('x', 200),
            cpuTemperatureCelsius: null,
            gpuTemperatureCelsius: null,
            fan1Rpm: 2_300,
            fan2Rpm: 2_500);

        Assert.StartsWith("OpenBlade · Battery · Battery Saver · Fan 1 2,300 RPM", result);
        Assert.DoesNotContain(new string('x', 10), result, StringComparison.Ordinal);
        Assert.InRange(result.Length, 1, TrayHoverLabelFormatter.MaximumLength);
    }
}
