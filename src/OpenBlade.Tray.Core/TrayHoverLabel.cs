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
                $"Plugged in · {PerformancePresetPresentation.Label(performance)}",
            PowerSource.Battery =>
                $"Battery · {PerformancePresetPresentation.Label(performance)}",
            PowerSource.UsbC =>
                $"USB-C · {PerformancePresetPresentation.Label(performance)}",
            PowerSource.ExternalUnknown => "Checking power source",
            _ => "Power source unavailable",
        };
}
