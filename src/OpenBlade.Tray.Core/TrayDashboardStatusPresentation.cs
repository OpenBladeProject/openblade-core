using System.Globalization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static class TrayDashboardStatusPresentation
{
    public static string BatteryPower(
        bool connected,
        int? batteryPowerFlowMilliwatts,
        PowerSource powerSource)
    {
        if (!connected || batteryPowerFlowMilliwatts is not int milliwatts)
        {
            return "—";
        }

        if (milliwatts == 0)
        {
            return powerSource is PowerSource.PluggedIn or PowerSource.UsbC
                ? "Idle"
                : "—";
        }

        double watts = Math.Abs((long)milliwatts) / 1000d;
        string value = watts.ToString("0.0", CultureInfo.InvariantCulture);
        return milliwatts > 0 ? $"+{value} W" : $"{value} W";
    }

    public static string Temperatures(
        bool connected,
        int? cpuCelsius,
        int? gpuCelsius)
    {
        if (!connected)
        {
            return "—";
        }

        return (cpuCelsius, gpuCelsius) switch
        {
            (int cpu, int gpu) => $"{cpu}° / {gpu}°",
            (int cpu, null) => $"{cpu}°",
            (null, int gpu) => $"{gpu}°",
            _ => "—",
        };
    }

    public static string Fans(bool connected, FanMode fanMode) =>
        connected ? FanModePresentation.Label(fanMode) : "—";
}
