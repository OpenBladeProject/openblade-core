namespace OpenBlade.Tray;

public sealed record TelemetryChartMetricPreference(
    TelemetryMetric Metric,
    bool IsVisible = true,
    string? ColorHex = null);

/// <summary>Bounded per-user presentation choices, independent of telemetry capture.</summary>
public static class TelemetryChartPreferenceDefaults
{
    private static readonly TelemetryMetric[] Metrics =
    [
        TelemetryMetric.CpuTemperatureCelsius,
        TelemetryMetric.GpuTemperatureCelsius,
        TelemetryMetric.BatteryPowerFlowMilliwatts,
        TelemetryMetric.Fan1Rpm,
        TelemetryMetric.Fan2Rpm,
    ];

    public static IReadOnlyList<TelemetryChartMetricPreference> Normalize(
        IReadOnlyList<TelemetryChartMetricPreference>? preferences)
    {
        Dictionary<TelemetryMetric, TelemetryChartMetricPreference> accepted = [];
        foreach (TelemetryChartMetricPreference? preference in preferences?.Take(32) ?? [])
        {
            if (preference is null || !Enum.IsDefined(preference.Metric)
                || accepted.ContainsKey(preference.Metric))
            {
                continue;
            }
            accepted.Add(preference.Metric, preference with
            {
                ColorHex = NormalizeColor(preference.ColorHex),
            });
        }
        TelemetryChartMetricPreference[] result = Metrics.Select(metric =>
            accepted.GetValueOrDefault(metric) ?? new TelemetryChartMetricPreference(metric)).ToArray();
        if (!result.Any(preference => preference.IsVisible))
        {
            result[0] = result[0] with { IsVisible = true };
        }
        return result;
    }

    public static string? NormalizeColor(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#'
            || !value.AsSpan(1).ContainsOnlyHexCharacters())
        {
            return null;
        }
        return value.ToUpperInvariant();
    }

    private static bool ContainsOnlyHexCharacters(this ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }
}
