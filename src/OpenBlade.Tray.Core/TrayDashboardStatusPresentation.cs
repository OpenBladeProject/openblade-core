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

    public static string PowerSourceText(bool connected, PowerSource powerSource) =>
        !connected
            ? "—"
            : powerSource switch
            {
                OpenBlade.Contracts.PowerSource.PluggedIn => "AC",
                OpenBlade.Contracts.PowerSource.UsbC => "USB-C",
                OpenBlade.Contracts.PowerSource.Battery => "Battery",
                _ => "—",
            };

    public static string FanSpeed(bool connected, int? fan1Rpm, int? fan2Rpm)
    {
        if (!connected)
        {
            return "—";
        }

        return (fan1Rpm, fan2Rpm) switch
        {
            (int fan1, int fan2) => $"{FormatFanSpeed(fan1)} / {FormatFanSpeed(fan2)}",
            (int fan1, null) => FormatFanSpeed(fan1),
            (null, int fan2) => FormatFanSpeed(fan2),
            _ => "—",
        };
    }

    private static string FormatFanSpeed(int rpm) => rpm < 1000
        ? rpm.ToString(CultureInfo.InvariantCulture)
        : $"{(rpm / 1000d).ToString("0.#", CultureInfo.InvariantCulture)}K";
}
