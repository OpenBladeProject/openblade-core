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

public sealed record TrayHoverLabelFormatResult(
    string Text,
    IReadOnlyList<TrayHoverLabelItemKind> OmittedItems)
{
    public bool HasOmittedItems => OmittedItems.Count > 0;
}

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
        int? fan2Rpm) => FormatDetailed(
            items,
            isConnected,
            powerSource,
            performance,
            batteryPowerFlow,
            cpuTemperatureCelsius,
            gpuTemperatureCelsius,
            fan1Rpm,
            fan2Rpm).Text;

    public static TrayHoverLabelFormatResult FormatDetailed(
        IEnumerable<TrayHoverLabelItemKind> items,
        bool isConnected,
        PowerSource powerSource,
        PerformancePreset performance,
        string batteryPowerFlow,
        int? cpuTemperatureCelsius,
        int? gpuTemperatureCelsius,
        int? fan1Rpm,
        int? fan2Rpm,
        string? performanceLabelOverride = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        TrayHoverLabelItemKind[] selectedItems = items
            .Where(Enum.IsDefined)
            .Distinct()
            .ToArray();
        bool showAppName = selectedItems.Contains(TrayHoverLabelItemKind.AppName);
        if (!isConnected)
        {
            return new TrayHoverLabelFormatResult(
                showAppName
                    ? TrayText.Get("OpenBlade · Service unavailable")
                    : TrayText.Get("Service unavailable"),
                []);
        }

        (TrayHoverLabelItemKind Kind, string Value)[] details = selectedItems
            .Where(item => item != TrayHoverLabelItemKind.AppName)
            .Select(item => (Kind: item, Value: item switch
            {
                TrayHoverLabelItemKind.ActiveProfile =>
                    FormatProfile(powerSource, performance, performanceLabelOverride),
                TrayHoverLabelItemKind.PowerSource => FormatPowerSource(powerSource),
                TrayHoverLabelItemKind.PowerMode => performanceLabelOverride
                    ?? PerformancePresetPresentation.Label(performance),
                TrayHoverLabelItemKind.BatteryPower => batteryPowerFlow,
                TrayHoverLabelItemKind.CpuTemperature => FormatTemperature("CPU", cpuTemperatureCelsius),
                // A missing NVIDIA reading means the discrete GPU is unavailable
                // or powered down. Omit it instead of implying a live sensor.
                TrayHoverLabelItemKind.GpuTemperature => gpuTemperatureCelsius is int
                    ? FormatTemperature("GPU", gpuTemperatureCelsius)
                    : string.Empty,
                TrayHoverLabelItemKind.Fan1Speed =>
                    FormatFan(TrayText.Get("Fan 1"), fan1Rpm),
                TrayHoverLabelItemKind.Fan2Speed =>
                    FormatFan(TrayText.Get("Fan 2"), fan2Rpm),
                _ => throw new ArgumentOutOfRangeException(nameof(items)),
            }))
            .Where(detail => !string.IsNullOrWhiteSpace(detail.Value))
            .ToArray();

        string result = showAppName ? "OpenBlade" : string.Empty;
        List<TrayHoverLabelItemKind> omittedItems = [];
        for (int index = 0; index < details.Length; index++)
        {
            string detail = details[index].Value;
            string candidate = string.IsNullOrEmpty(result)
                ? detail
                : TrayText.Format("{0} · {1}", result, detail);
            if (candidate.Length > MaximumLength)
            {
                omittedItems.AddRange(details[index..].Select(value => value.Kind));
                break;
            }
            result = candidate;
        }
        return new TrayHoverLabelFormatResult(
            string.IsNullOrWhiteSpace(result) ? "OpenBlade" : result,
            omittedItems);
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
            ? TrayText.Get("Service unavailable")
            : mode switch
            {
                TrayHoverLabelMode.OpenBlade => string.Empty,
                TrayHoverLabelMode.ActiveProfile => FormatProfile(powerSource, performance),
                TrayHoverLabelMode.BatteryPowerFlow => batteryPowerFlow,
                TrayHoverLabelMode.ProfileAndPowerFlow =>
                    TrayText.Format(
                        "{0} · {1}",
                        FormatProfile(powerSource, performance),
                        batteryPowerFlow),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
        string result = string.IsNullOrEmpty(detail)
            ? "OpenBlade"
            : TrayText.Format("OpenBlade · {0}", detail);
        return result.Length <= MaximumLength
            ? result
            : result[..MaximumLength];
    }

    private static string FormatProfile(
        PowerSource powerSource,
        PerformancePreset performance,
        string? performanceLabelOverride = null)
    {
        string performanceLabel = performanceLabelOverride
            ?? PerformancePresetPresentation.Label(performance);
        return powerSource switch
        {
            PowerSource.PluggedIn =>
                TrayText.Format(
                    "AC · {0}",
                    performanceLabel),
            PowerSource.Battery =>
                TrayText.Format(
                    "Battery · {0}",
                    performanceLabel),
            PowerSource.UsbC =>
                TrayText.Format(
                    "USB-C · {0}",
                    performanceLabel),
            PowerSource.ExternalUnknown => PowerSourcePresentation.Label(powerSource),
            _ => TrayText.Get("Power source unavailable"),
        };
    }

    private static string FormatPowerSource(PowerSource powerSource) =>
        PowerSourcePresentation.Label(powerSource);

    private static string FormatTemperature(string label, int? value) => value is int celsius
        ? TrayText.Format("{0} {1} °C", label, celsius)
        : TrayText.Format("{0} unavailable", label);

    private static string FormatFan(string label, int? value) => value is int rpm
        ? TrayText.Format("{0} {1:N0} RPM", label, rpm)
        : TrayText.Format("{0} unavailable", label);
}
