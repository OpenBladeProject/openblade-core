using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryChartPreferenceTests
{
    [Fact]
    public void MissingPreferencesRetainEveryMetricWithThemeColors()
    {
        var preferences = TelemetryChartPreferenceDefaults.Normalize(null);
        Assert.Equal(5, preferences.Count);
        Assert.All(preferences, preference =>
        {
            Assert.True(preference.IsVisible);
            Assert.Null(preference.ColorHex);
        });
    }

    [Fact]
    public void InvalidAndDuplicatePreferencesCannotHideAllMetricsOrSetInvalidColors()
    {
        TelemetryChartMetricPreference[] preferences = Enum.GetValues<TelemetryMetric>()
            .Select(metric => new TelemetryChartMetricPreference(metric, false, "invalid"))
            .Concat([new((TelemetryMetric)99), new(TelemetryMetric.Fan1Rpm, true, "#AABBCC")])
            .ToArray();
        var normalized = TelemetryChartPreferenceDefaults.Normalize(preferences);
        Assert.Equal(5, normalized.Count);
        Assert.True(normalized.Single(value => value.Metric == TelemetryMetric.CpuTemperatureCelsius).IsVisible);
        Assert.False(normalized.Single(value => value.Metric == TelemetryMetric.Fan1Rpm).IsVisible);
        Assert.All(normalized, value => Assert.Null(value.ColorHex));
    }

    [Theory]
    [InlineData("#1a2B3c", "#1A2B3C")]
    [InlineData("#000000", "#000000")]
    [InlineData("#ffffffff", null)]
    [InlineData("#12Z456", null)]
    [InlineData("red", null)]
    public void ColorsAreCanonicalOpaqueRgb(string input, string? expected) =>
        Assert.Equal(expected, TelemetryChartPreferenceDefaults.NormalizeColor(input));

    [Fact]
    public async Task ChartChoicesRoundTripAlongsideExistingUserSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"OpenBlade-telemetry-{Guid.NewGuid():N}.json");
        try
        {
            var store = new UiPreferenceStore(path);
            await store.SaveAsync(UiPreferences.Default with
            {
                StartMinimized = true,
                TelemetryChartMetrics = [new(TelemetryMetric.Fan1Rpm, false, "#AABBCC")],
            });
            UiPreferences loaded = await store.LoadAsync();
            Assert.True(loaded.StartMinimized);
            var fan = loaded.EffectiveTelemetryChartMetrics.Single(value => value.Metric == TelemetryMetric.Fan1Rpm);
            Assert.False(fan.IsVisible);
            Assert.Equal("#AABBCC", fan.ColorHex);
            Assert.Equal(5, loaded.EffectiveTelemetryChartMetrics.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnknownNamedMetricPreservesOtherPreferencesAndValidMetricEntries()
    {
        string path = Path.Combine(Path.GetTempPath(), $"OpenBlade-telemetry-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "theme": "Dark",
                  "startMinimized": true,
                  "customAccentColor": "#123456",
                  "telemetryChartMetrics": [
                    { "metric": "Fan3Rpm", "isVisible": false, "colorHex": "#112233" },
                    { "metric": 99, "isVisible": false, "colorHex": "#445566" },
                    { "metric": "Fan1Rpm", "isVisible": false, "colorHex": "#aabbcc" }
                  ]
                }
                """);

            UiPreferences loaded = await new UiPreferenceStore(path).LoadAsync();

            Assert.Equal(ThemePreference.Dark, loaded.Theme);
            Assert.True(loaded.StartMinimized);
            Assert.Equal("#123456", loaded.CustomAccentColor);
            IReadOnlyList<TelemetryChartMetricPreference> metrics =
                loaded.EffectiveTelemetryChartMetrics;
            Assert.Equal(5, metrics.Count);
            Assert.All(metrics, value => Assert.True(Enum.IsDefined(value.Metric)));
            Assert.True(metrics.Single(value =>
                value.Metric == TelemetryMetric.CpuTemperatureCelsius).IsVisible);
            TelemetryChartMetricPreference fan = metrics.Single(value =>
                value.Metric == TelemetryMetric.Fan1Rpm);
            Assert.False(fan.IsVisible);
            Assert.Equal("#AABBCC", fan.ColorHex);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
