using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum TrayHoverLabelMode
{
    OpenBlade,
    ActiveProfile,
    BatteryPowerFlow,
    ProfileAndPowerFlow,
}

public sealed record TrayHoverLabelOption(
    TrayHoverLabelMode Mode,
    string Label);

public static class TrayHoverLabelFormatter
{
    public const int MaximumLength = 63;

    public static string Format(
        IEnumerable<TrayHoverLabelItemKind> items,
        bool isConnected,
        PowerSource powerSource,
        PerformancePreset performance,
        string batteryPowerFlow,
        int? cpuTemperatureCelsius,
        int? gpuTemperatureCelsius,
        int? fan1Rpm,
        int? fan2Rpm)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!isConnected)
        {
            return "OpenBlade · Service unavailable";
        }

        string[] details = items
            .Where(Enum.IsDefined)
            .Distinct()
            .Select(item => item switch
            {
                TrayHoverLabelItemKind.ActiveProfile =>
                    FormatProfile(powerSource, performance),
                TrayHoverLabelItemKind.PowerSource => FormatPowerSource(powerSource),
                TrayHoverLabelItemKind.PowerMode =>
                    PerformancePresetPresentation.Label(performance),
                TrayHoverLabelItemKind.BatteryPower => batteryPowerFlow,
                TrayHoverLabelItemKind.CpuTemperature =>
                    FormatTemperature("CPU", cpuTemperatureCelsius),
                TrayHoverLabelItemKind.GpuTemperature =>
                    FormatTemperature("GPU", gpuTemperatureCelsius),
                TrayHoverLabelItemKind.Fan1Speed => FormatFan("Fan 1", fan1Rpm),
                TrayHoverLabelItemKind.Fan2Speed => FormatFan("Fan 2", fan2Rpm),
                _ => throw new ArgumentOutOfRangeException(nameof(items)),
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        string result = "OpenBlade";
        foreach (string detail in details)
        {
            string candidate = $"{result} · {detail}";
            if (candidate.Length > MaximumLength)
            {
                break;
            }
            result = candidate;
        }
        return result;
    }

    public static string Format(
        TrayHoverLabelMode mode,
        bool isConnected,
        PowerSource powerSource,
        PerformancePreset performance,
        string batteryPowerFlow)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        string detail = !isConnected
            ? "Service unavailable"
            : mode switch
            {
                TrayHoverLabelMode.OpenBlade => string.Empty,
                TrayHoverLabelMode.ActiveProfile => FormatProfile(powerSource, performance),
                TrayHoverLabelMode.BatteryPowerFlow => batteryPowerFlow,
                TrayHoverLabelMode.ProfileAndPowerFlow =>
                    $"{FormatProfile(powerSource, performance)} · {batteryPowerFlow}",
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
        string result = string.IsNullOrEmpty(detail)
            ? "OpenBlade"
            : $"OpenBlade · {detail}";
        return result.Length <= MaximumLength
            ? result
            : result[..MaximumLength];
    }

    private static string FormatProfile(
        PowerSource powerSource,
        PerformancePreset performance) => powerSource switch
        {
            PowerSource.PluggedIn =>
                $"AC · {PerformancePresetPresentation.Label(performance)}",
            PowerSource.Battery =>
                $"Battery · {PerformancePresetPresentation.Label(performance)}",
            PowerSource.UsbC =>
                $"USB-C · {PerformancePresetPresentation.Label(performance)}",
            PowerSource.ExternalUnknown => "Checking power source",
            _ => "Power source unavailable",
        };

    private static string FormatPowerSource(PowerSource powerSource) => powerSource switch
    {
        PowerSource.PluggedIn => "AC",
        PowerSource.Battery => "Battery",
        PowerSource.UsbC => "USB-C",
        PowerSource.ExternalUnknown => "Checking power source",
        _ => "Power source unavailable",
    };

    private static string FormatTemperature(string label, int? value) => value is int celsius
        ? $"{label} {celsius} °C"
        : $"{label} unavailable";

    private static string FormatFan(string label, int? value) => value is int rpm
        ? $"{label} {rpm:N0} RPM"
        : $"{label} unavailable";
}
