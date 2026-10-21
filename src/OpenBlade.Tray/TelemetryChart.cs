using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Brush = Microsoft.UI.Xaml.Media.Brush;
using ComboBox = Microsoft.UI.Xaml.Controls.ComboBox;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Point = Windows.Foundation.Point;

namespace OpenBlade.Tray;

/// <summary>A single-unit chart with a keyboard-accessible sample readout.</summary>
public sealed class TelemetryChart : Grid
{
    public static readonly DependencyProperty PlotStrokeProperty = DependencyProperty.Register(
        nameof(PlotStroke), typeof(Brush), typeof(TelemetryChart),
        new PropertyMetadata(null, (sender, _) => ((TelemetryChart)sender).Draw()));
    public static readonly DependencyProperty GridStrokeProperty = DependencyProperty.Register(
        nameof(GridStroke), typeof(Brush), typeof(TelemetryChart),
        new PropertyMetadata(null, (sender, _) => ((TelemetryChart)sender).Draw()));

    public Brush? PlotStroke
    {
        get => (Brush?)GetValue(PlotStrokeProperty);
        set => SetValue(PlotStrokeProperty, value);
    }

    public Brush? GridStroke
    {
        get => (Brush?)GetValue(GridStrokeProperty);
        set => SetValue(GridStrokeProperty, value);
    }
    private sealed record MetricChoice(TelemetryMetric Metric, string Label, string Unit, double Divisor = 1);
    private readonly ComboBox metric;
    private readonly Canvas plot = new() { Height = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock statistics = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock timeRange = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock sampleReadout = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Slider cursor = new() { Minimum = 0, Maximum = 1, StepFrequency = 1, IsEnabled = false };
    private TelemetryHistorySnapshot? snapshot;
    private IReadOnlyList<TelemetryChartPoint> points = [];
    private bool followLatest = true;

    public TelemetryChart()
    {
        MetricChoice[] choices =
        [
            new(TelemetryMetric.BatteryPowerFlowMilliwatts, LocalizedResources.Get("Feature_c650cc78b7fe", "Battery power"), "W", 1000),
            new(TelemetryMetric.CpuTemperatureCelsius, LocalizedResources.Get("Feature_f8d2f2cd53a8", "CPU temperature"), "°C"),
            new(TelemetryMetric.GpuTemperatureCelsius, LocalizedResources.Get("Feature_905c2bc1b6eb", "GPU temperature"), "°C"),
            new(TelemetryMetric.Fan1Rpm, LocalizedResources.Get("Feature_6141eef71a99", "Fan 1 speed"), "RPM"),
            new(TelemetryMetric.Fan2Rpm, LocalizedResources.Get("Feature_c83b7fc012ef", "Fan 2 speed"), "RPM"),
        ];
        metric = new ComboBox
        {
            Header = LocalizedResources.Get("Feature_42d94a35c548", "Metric"),
            ItemsSource = choices,
            DisplayMemberPath = "Label",
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(metric);
        panel.Children.Add(plot);
        panel.Children.Add(timeRange);
        panel.Children.Add(statistics);
        panel.Children.Add(cursor);
        panel.Children.Add(sampleReadout);
        Children.Add(panel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cursor, LocalizedResources.Get("Feature_7f1c35cb6572", "Select telemetry sample"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(plot, LocalizedResources.Get("Feature_73998e0f0da8", "Telemetry chart. Use the sample slider for exact values."));
        metric.SelectionChanged += (_, _) => Draw();
        plot.SizeChanged += (_, _) => Draw();
        cursor.ValueChanged += (_, _) => UpdateReadout();
        cursor.PointerPressed += (_, _) => followLatest = false;
        cursor.KeyDown += (_, _) => followLatest = false;
        plot.PointerMoved += (_, args) =>
        {
            if (points.Count > 0 && plot.ActualWidth > 0)
            {
                double fraction = Math.Clamp(args.GetCurrentPoint(plot).Position.X / plot.ActualWidth, 0, 1);
                followLatest = false;
                DateTimeOffset target = points[0].TimestampUtc +
                    TimeSpan.FromTicks((long)((points[^1].TimestampUtc - points[0].TimestampUtc).Ticks * fraction));
                cursor.Value = NearestPoint(target);
            }
        };
        ActualThemeChanged += (_, _) => Draw();
    }

    public void Update(TelemetryHistorySnapshot value)
    {
        snapshot = value;
        Draw();
    }

    private void Draw()
    {
        plot.Children.Clear();
        if (metric.SelectedItem is not MetricChoice choice || snapshot is null)
        {
            return;
        }
        TelemetrySeriesSnapshot? series = snapshot.Series.FirstOrDefault(item => item.Metric == choice.Metric);
        DateTimeOffset? selectedTime = points.Count == 0 ? null : points[Math.Clamp((int)cursor.Value, 0, points.Count - 1)].TimestampUtc;
        points = series?.Points ?? [];
        cursor.Maximum = Math.Max(1, points.Count - 1);
        cursor.IsEnabled = points.Count > 0;
        cursor.Value = followLatest ? Math.Max(0, points.Count - 1)
            : selectedTime is { } selected ? NearestPoint(selected) : 0;
        TelemetryMetricStatistics? stats = series?.Statistics;
        statistics.Text = stats?.Count > 0
            ? LocalizedResources.Format("Feature_4696d358d054", "Min {0} · Max {1} · Average {2} · {3} readings",
                Format(stats.Minimum), Format(stats.Maximum), Format(stats.Average), stats.Count)
            : LocalizedResources.Get("Feature_3ca3281778c2", "No readings are available for this metric.");
        timeRange.Text = snapshot.RangeStartUtc is { } start && snapshot.RangeEndUtc is { } end
            ? $"{start.ToLocalTime():G} – {end.ToLocalTime():G}" : string.Empty;
        UpdateReadout();
        double width = plot.ActualWidth;
        double height = plot.Height;
        if (width <= 0 || stats?.Minimum is not { } minimum || stats.Maximum is not { } maximum || points.Count == 0)
        {
            return;
        }
        double padding = Math.Max(1, (maximum - minimum) * 0.08);
        double low = minimum - padding;
        double high = maximum + padding;
        if (PlotStroke is not { } brush || GridStroke is not { } gridBrush)
        {
            return;
        }
        for (int i = 0; i <= 4; i++)
        {
            plot.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = height * i / 4,
                Y2 = height * i / 4,
                Stroke = gridBrush,
                StrokeThickness = 1,
            });
        }
        DateTimeOffset first = points[0].TimestampUtc;
        double duration = Math.Max(1, (points[^1].TimestampUtc - first).TotalMilliseconds);
        foreach (IGrouping<DateTimeOffset, TelemetryEvent> markers in snapshot.Events
            .Where(marker => marker.TimestampUtc >= first && marker.TimestampUtc <= points[^1].TimestampUtc)
            .GroupBy(marker => marker.TimestampUtc).TakeLast(128))
        {
            double x = (markers.Key - first).TotalMilliseconds / duration * width;
            var marker = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = height,
                Stroke = gridBrush,
                StrokeThickness = 2,
                StrokeDashArray = [3, 3],
            };
            string description = string.Join(Environment.NewLine, markers.Select(value =>
                $"{value.TimestampUtc.ToLocalTime():G}: {value.PreviousValue} → {value.CurrentValue}"));
            ToolTipService.SetToolTip(marker, description);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(marker, description);
            plot.Children.Add(marker);
        }
        Polyline? segment = null;
        foreach (TelemetryChartPoint point in points)
        {
            if (point.Value is not { } value)
            {
                segment = null;
                continue;
            }
            if (segment is null)
            {
                segment = new Polyline { Stroke = brush, StrokeThickness = 2 };
                plot.Children.Add(segment);
            }
            segment.Points.Add(new Point((point.TimestampUtc - first).TotalMilliseconds / duration * width,
                height - (value - low) / (high - low) * height));
        }

        string Format(double? value) => value is { } number
            ? $"{number / choice.Divisor:0.##} {choice.Unit}" : LocalizedResources.Get("Feature_b4c2f503b194", "Unavailable");
    }

    private void UpdateReadout()
    {
        if (points.Count == 0 || metric.SelectedItem is not MetricChoice choice)
        {
            sampleReadout.Text = LocalizedResources.Get("Feature_7d021f67142c", "No sample selected.");
            return;
        }
        TelemetryChartPoint point = points[Math.Clamp((int)Math.Round(cursor.Value), 0, points.Count - 1)];
        string value = point.Value is { } number
            ? $"{number / choice.Divisor:0.##} {choice.Unit}" : LocalizedResources.Get("Feature_b4c2f503b194", "Unavailable");
        sampleReadout.Text = $"{point.TimestampUtc.ToLocalTime():G} · {choice.Label}: {value}";
    }

    private int NearestPoint(DateTimeOffset target)
    {
        int nearest = 0;
        long distance = long.MaxValue;
        for (int index = 0; index < points.Count; index++)
        {
            long candidate = Math.Abs((points[index].TimestampUtc - target).Ticks);
            if (candidate < distance)
            {
                nearest = index;
                distance = candidate;
            }
        }
        return nearest;
    }
}
