using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Tray;

public sealed record TelemetryChartMetricPreference(
    [property: JsonConverter(typeof(TelemetryChartMetricPreferenceJsonConverter))]
    TelemetryMetric Metric,
    bool IsVisible = true,
    string? ColorHex = null);

internal sealed class TelemetryChartMetricPreferenceJsonConverter : JsonConverter<TelemetryMetric>
{
    private const int UnsupportedMetric = -1;

    public TelemetryChartMetricPreferenceJsonConverter()
    {
    }

    public override TelemetryMetric Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? name = reader.GetString();
            return Enum.TryParse(name, ignoreCase: true, out TelemetryMetric metric)
                && Enum.IsDefined(metric)
                    ? metric
                    : (TelemetryMetric)UnsupportedMetric;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
        {
            return (TelemetryMetric)value;
        }

        throw new JsonException("A telemetry chart metric must be a name or integer.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        TelemetryMetric value,
        JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value))
        {
            writer.WriteStringValue(value.ToString());
            return;
        }

        writer.WriteNumberValue((int)value);
    }
}

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
