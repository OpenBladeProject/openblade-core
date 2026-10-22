using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Brush = Microsoft.UI.Xaml.Media.Brush;
using Button = Microsoft.UI.Xaml.Controls.Button;
using ComboBox = Microsoft.UI.Xaml.Controls.ComboBox;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Point = Windows.Foundation.Point;

namespace OpenBlade.Tray;

/// <summary>A timestamped telemetry chart with exact raw-sample inspection.</summary>
public sealed class TelemetryChart : Grid
{
    private const double PlotLeft = 76;
    private const double PlotTop = 12;
    private const double PlotRight = 12;
    private const double PlotBottom = 34;

    public static readonly DependencyProperty PlotStrokeProperty = DependencyProperty.Register(
        nameof(PlotStroke), typeof(Brush), typeof(TelemetryChart),
        new PropertyMetadata(null, (sender, _) => ((TelemetryChart)sender).Draw()));
    public static readonly DependencyProperty GridStrokeProperty = DependencyProperty.Register(
        nameof(GridStroke), typeof(Brush), typeof(TelemetryChart),
        new PropertyMetadata(null, (sender, _) => ((TelemetryChart)sender).Draw()));

    private sealed record MetricChoice(
        TelemetryMetric Metric,
        string Label,
        string Unit,
        double Divisor = 1);

    private readonly MetricChoice[] choices;
    private readonly ComboBox metric;
    private readonly Canvas plot = new()
    {
        Height = 264,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private readonly TextBlock statistics = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock inspectionState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock selectedMetricReadout = new()
    {
        FontSize = 18,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly StackPanel sampleReadout = new() { Spacing = 2 };
    private readonly Dictionary<TelemetryMetric, TextBlock> metricReadouts = [];
    private readonly Button previousSampleButton;
    private readonly Button nextSampleButton;
    private readonly Button followLatestButton;
    private readonly Line crosshair = new() { StrokeThickness = 1.5 };
    private readonly Ellipse selectedPoint = new() { Width = 8, Height = 8 };
    private TelemetryHistorySnapshot? snapshot;
    private IReadOnlyList<TelemetryChartPoint> points = [];
    private IReadOnlyList<TelemetrySample> rawSamples = [];
    private TelemetryChartScale? scale;
    private DateTimeOffset? visibleStartUtc;
    private DateTimeOffset? visibleEndUtc;
    private int selectedSampleIndex = -1;
    private bool followLatest = true;
    private double plotAreaWidth;
    private double plotAreaHeight;
#if OPENBLADE_DEVELOPER_HARNESS
    private double? localizationSnapshotInspectionFraction;
    private bool localizationSnapshotFocusNextSample;
#endif

    public TelemetryChart()
    {
        choices =
        [
            new(TelemetryMetric.BatteryPowerFlowMilliwatts,
                LocalizedResources.Get("TelemetryUI_BatteryPower", "Battery power"), "W", 1000),
            new(TelemetryMetric.CpuTemperatureCelsius,
                LocalizedResources.Get("TelemetryUI_CpuTemperature", "CPU temperature"), "°C"),
            new(TelemetryMetric.GpuTemperatureCelsius,
                LocalizedResources.Get("TelemetryUI_GpuTemperature", "GPU temperature"), "°C"),
            new(TelemetryMetric.Fan1Rpm,
                LocalizedResources.Get("TelemetryUI_Fan1Speed", "Fan 1 speed"), "RPM"),
            new(TelemetryMetric.Fan2Rpm,
                LocalizedResources.Get("TelemetryUI_Fan2Speed", "Fan 2 speed"), "RPM"),
        ];
        metric = new ComboBox
        {
            Header = LocalizedResources.Get("TelemetryUI_Metric", "Metric"),
            ItemsSource = choices,
            DisplayMemberPath = "Label",
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        previousSampleButton = CreateInspectionButton(
            LocalizedResources.Get("TelemetryUI_PreviousSample", "Previous sample"));
        nextSampleButton = CreateInspectionButton(
            LocalizedResources.Get("TelemetryUI_NextSample", "Next sample"));
        followLatestButton = CreateInspectionButton(
            LocalizedResources.Get("TelemetryUI_FollowLatest", "Follow latest"));

        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.Children.Add(previousSampleButton);
        Grid.SetColumn(nextSampleButton, 1);
        controls.Children.Add(nextSampleButton);
        Grid.SetColumn(followLatestButton, 2);
        controls.Children.Add(followLatestButton);

        foreach (MetricChoice choice in choices)
        {
            var readout = new TextBlock { TextWrapping = TextWrapping.Wrap };
            metricReadouts.Add(choice.Metric, readout);
            sampleReadout.Children.Add(readout);
        }

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(metric);
        panel.Children.Add(plot);
        panel.Children.Add(statistics);
        panel.Children.Add(inspectionState);
        panel.Children.Add(selectedMetricReadout);
        panel.Children.Add(sampleReadout);
        panel.Children.Add(controls);
        Children.Add(panel);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            plot,
            LocalizedResources.Get(
                "TelemetryUI_ChartAutomationName",
                "Telemetry timeline. Point to the chart or use the sample buttons for exact readings."));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            sampleReadout,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        metric.SelectionChanged += (_, _) => Draw();
        plot.SizeChanged += (_, _) => Draw();
        plot.PointerMoved += OnPlotPointerMoved;
        plot.PointerPressed += OnPlotPointerPressed;
        previousSampleButton.Click += (_, _) => MoveSelection(-1);
        nextSampleButton.Click += (_, _) => MoveSelection(1);
        followLatestButton.Click += (_, _) => FollowLatest();
        ActualThemeChanged += (_, _) => Draw();
    }

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

    public void Update(
        TelemetryHistorySnapshot value,
        DateTimeOffset? visibleStartUtc = null,
        DateTimeOffset? visibleEndUtc = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (visibleStartUtc.HasValue != visibleEndUtc.HasValue)
        {
            throw new ArgumentException("Both visible telemetry time bounds must be supplied together.");
        }
        if (visibleStartUtc > visibleEndUtc)
        {
            throw new ArgumentException("Visible telemetry start must not follow its end.");
        }

        snapshot = value;
        this.visibleStartUtc = visibleStartUtc;
        this.visibleEndUtc = visibleEndUtc;
        Draw();
    }

#if OPENBLADE_DEVELOPER_HARNESS
    internal void ShowLocalizationSnapshotInspection(
        double fraction,
        bool focusNextSampleButton = false)
    {
        localizationSnapshotInspectionFraction = Math.Clamp(fraction, 0, 1);
        localizationSnapshotFocusNextSample = focusNextSampleButton;
        ApplyLocalizationSnapshotInspection();
    }
#endif

    private static Button CreateInspectionButton(string content)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = content,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 44,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, content);
        return button;
    }

    private void Draw()
    {
        DateTimeOffset? retainedSelection = SelectedSample?.TimestampUtc;
        plot.Children.Clear();
        scale = null;
        if (metric.SelectedItem is not MetricChoice choice || snapshot is null)
        {
            return;
        }

        TelemetrySeriesSnapshot? series = snapshot.Series.FirstOrDefault(
            item => item.Metric == choice.Metric);
        points = series?.Points ?? [];
        rawSamples = snapshot.RawSamples;
        selectedSampleIndex = ResolveSelectedIndex(retainedSelection);
        UpdateSummary(choice, series?.Statistics);

        double width = plot.ActualWidth;
        double height = plot.Height;
        plotAreaWidth = width - PlotLeft - PlotRight;
        plotAreaHeight = height - PlotTop - PlotBottom;
        DateTimeOffset? startUtc = visibleStartUtc ?? snapshot.RangeStartUtc;
        DateTimeOffset? endUtc = visibleEndUtc ?? snapshot.RangeEndUtc;
        if (width <= PlotLeft + PlotRight || plotAreaHeight <= 0
            || startUtc is null || endUtc is null
            || PlotStroke is not { } plotBrush || GridStroke is not { } gridBrush)
        {
            UpdateInspection();
            return;
        }

        double minimum = series?.Statistics.Minimum ?? 0;
        double maximum = series?.Statistics.Maximum ?? 1;
        scale = TelemetryChartScale.Create(
            startUtc.Value,
            endUtc.Value,
            minimum,
            maximum);
        DrawGrid(choice, series?.Statistics.Count > 0, gridBrush);
        DrawEvents(gridBrush);
        DrawSeries(plotBrush);

        crosshair.Stroke = plotBrush;
        selectedPoint.Fill = plotBrush;
        plot.Children.Add(crosshair);
        plot.Children.Add(selectedPoint);
        UpdateInspection();
#if OPENBLADE_DEVELOPER_HARNESS
        ApplyLocalizationSnapshotInspection();
#endif
    }

#if OPENBLADE_DEVELOPER_HARNESS
    private void ApplyLocalizationSnapshotInspection()
    {
        if (scale is null || localizationSnapshotInspectionFraction is not { } fraction)
        {
            return;
        }

        localizationSnapshotInspectionFraction = null;
        InspectAtFraction(fraction);
        if (localizationSnapshotFocusNextSample)
        {
            _ = nextSampleButton.Focus(FocusState.Keyboard);
        }
        localizationSnapshotFocusNextSample = false;
    }
#endif

    private int ResolveSelectedIndex(DateTimeOffset? retainedSelection)
    {
        if (rawSamples.Count == 0)
        {
            return -1;
        }
        if (followLatest)
        {
            return rawSamples.Count - 1;
        }
        return retainedSelection is { } selected
            ? TelemetryChartSelection.FindNearestSampleIndex(rawSamples, selected)
            : 0;
    }

    private void DrawGrid(MetricChoice choice, bool hasValues, Brush gridBrush)
    {
        if (scale is null)
        {
            return;
        }

        if (hasValues)
        {
            foreach (double tick in scale.CreateValueTicks(5))
            {
                double y = PlotTop + (scale.ValueFractionFromTop(tick) * plotAreaHeight);
                plot.Children.Add(new Line
                {
                    X1 = PlotLeft,
                    X2 = PlotLeft + plotAreaWidth,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                });
                var label = new TextBlock
                {
                    FontSize = 11,
                    Text = $"{tick / choice.Divisor:0.##} {choice.Unit}",
                    TextAlignment = TextAlignment.Right,
                    Width = PlotLeft - 8,
                };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 9);
                plot.Children.Add(label);
            }
        }

        int tickCount = plotAreaWidth >= 520 ? 5 : 3;
        TimeSpan duration = scale.EndUtc - scale.StartUtc;
        foreach (DateTimeOffset tick in scale.CreateTimeTicks(tickCount))
        {
            double x = PlotLeft + (scale.TimeFraction(tick) * plotAreaWidth);
            plot.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = PlotTop,
                Y2 = PlotTop + plotAreaHeight,
                Stroke = gridBrush,
                StrokeThickness = 1,
            });
            var label = new TextBlock
            {
                FontSize = 11,
                Text = FormatTimeTick(tick.ToLocalTime(), duration),
                TextAlignment = TextAlignment.Center,
                Width = 90,
            };
            Canvas.SetLeft(label, Math.Clamp(x - 45, 0, Math.Max(0, plot.ActualWidth - 90)));
            Canvas.SetTop(label, PlotTop + plotAreaHeight + 6);
            plot.Children.Add(label);
        }
    }

    private void DrawEvents(Brush gridBrush)
    {
        if (snapshot is null || scale is null)
        {
            return;
        }

        foreach (IGrouping<DateTimeOffset, TelemetryEvent> markers in snapshot.Events
            .Where(marker => marker.TimestampUtc >= scale.StartUtc
                && marker.TimestampUtc <= scale.EndUtc)
            .GroupBy(marker => marker.TimestampUtc)
            .TakeLast(128))
        {
            double x = PlotLeft + (scale.TimeFraction(markers.Key) * plotAreaWidth);
            var marker = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = PlotTop,
                Y2 = PlotTop + plotAreaHeight,
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
    }

    private void DrawSeries(Brush plotBrush)
    {
        if (scale is null)
        {
            return;
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
                segment = new Polyline { Stroke = plotBrush, StrokeThickness = 2 };
                plot.Children.Add(segment);
            }
            segment.Points.Add(new Point(
                PlotLeft + (scale.TimeFraction(point.TimestampUtc) * plotAreaWidth),
                PlotTop + (scale.ValueFractionFromTop(value) * plotAreaHeight)));
        }
    }

    private void UpdateSummary(MetricChoice choice, TelemetryMetricStatistics? metricStatistics)
    {
        statistics.Text = metricStatistics?.Count > 0
            ? LocalizedResources.Format(
                "TelemetryUI_Statistics",
                "Min {0} · Max {1} · Average {2} · {3} readings",
                FormatValue(metricStatistics.Minimum, choice),
                FormatValue(metricStatistics.Maximum, choice),
                FormatValue(metricStatistics.Average, choice),
                metricStatistics.Count)
            : LocalizedResources.Get(
                "TelemetryUI_NoReadings",
                "No readings are available for this metric.");
    }

    private void UpdateInspection()
    {
        TelemetrySample? sample = SelectedSample;
        bool hasSample = sample is not null;
        previousSampleButton.IsEnabled = selectedSampleIndex > 0;
        nextSampleButton.IsEnabled = selectedSampleIndex >= 0
            && selectedSampleIndex < rawSamples.Count - 1;
        followLatestButton.IsEnabled = hasSample && !followLatest;

        if (sample is null || metric.SelectedItem is not MetricChoice selectedChoice)
        {
            inspectionState.Text = LocalizedResources.Get(
                "TelemetryUI_NoSampleSelected",
                "No sample selected.");
            selectedMetricReadout.Text = string.Empty;
            sampleReadout.Visibility = Visibility.Collapsed;
            crosshair.Visibility = Visibility.Collapsed;
            selectedPoint.Visibility = Visibility.Collapsed;
            return;
        }

        string localTimestamp = sample.TimestampUtc.ToLocalTime().ToString("G");
        inspectionState.Text = followLatest
            ? LocalizedResources.Format(
                "TelemetryUI_FollowingLatest",
                "Following latest · {0}",
                localTimestamp)
            : LocalizedResources.Format(
                "TelemetryUI_InspectingSample",
                "Inspecting {0} · selection stays fixed",
                localTimestamp);
        selectedMetricReadout.Text = $"{selectedChoice.Label}: "
            + FormatValue(sample.Value(selectedChoice.Metric), selectedChoice);
        sampleReadout.Visibility = Visibility.Visible;
        foreach (MetricChoice choice in choices)
        {
            TextBlock readout = metricReadouts[choice.Metric];
            readout.Text = $"{choice.Label}: "
                + FormatValue(sample.Value(choice.Metric), choice);
            readout.Visibility = choice.Metric == selectedChoice.Metric
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            plot,
            $"{inspectionState.Text}. {selectedMetricReadout.Text}");
        if (scale is null)
        {
            crosshair.Visibility = Visibility.Collapsed;
            selectedPoint.Visibility = Visibility.Collapsed;
            return;
        }

        double x = PlotLeft + (scale.TimeFraction(sample.TimestampUtc) * plotAreaWidth);
        crosshair.X1 = x;
        crosshair.X2 = x;
        crosshair.Y1 = PlotTop;
        crosshair.Y2 = PlotTop + plotAreaHeight;
        crosshair.Visibility = Visibility.Visible;
        if (sample.Value(selectedChoice.Metric) is not { } selectedValue)
        {
            selectedPoint.Visibility = Visibility.Collapsed;
            return;
        }

        double y = PlotTop + (scale.ValueFractionFromTop(selectedValue) * plotAreaHeight);
        Canvas.SetLeft(selectedPoint, x - (selectedPoint.Width / 2));
        Canvas.SetTop(selectedPoint, y - (selectedPoint.Height / 2));
        selectedPoint.Visibility = Visibility.Visible;
    }

    private TelemetrySample? SelectedSample => selectedSampleIndex >= 0
        && selectedSampleIndex < rawSamples.Count
            ? rawSamples[selectedSampleIndex]
            : null;

    private void OnPlotPointerMoved(object sender, PointerRoutedEventArgs args) =>
        InspectAtPosition(args.GetCurrentPoint(plot).Position.X);

    private void OnPlotPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        InspectAtPosition(args.GetCurrentPoint(plot).Position.X);
        args.Handled = true;
    }

    private void InspectAtPosition(double x)
    {
        if (scale is null || rawSamples.Count == 0 || plotAreaWidth <= 0)
        {
            return;
        }

        InspectAtFraction((x - PlotLeft) / plotAreaWidth);
    }

    private void InspectAtFraction(double fraction)
    {
        if (scale is null || rawSamples.Count == 0)
        {
            return;
        }

        DateTimeOffset target = scale.TimestampAtFraction(fraction);
        selectedSampleIndex = TelemetryChartSelection.FindNearestSampleIndex(rawSamples, target);
        followLatest = false;
        UpdateInspection();
    }

    private void MoveSelection(int offset)
    {
        if (rawSamples.Count == 0)
        {
            return;
        }

        int current = selectedSampleIndex < 0 ? rawSamples.Count - 1 : selectedSampleIndex;
        selectedSampleIndex = Math.Clamp(current + offset, 0, rawSamples.Count - 1);
        followLatest = false;
        UpdateInspection();
    }

    private void FollowLatest()
    {
        if (rawSamples.Count == 0)
        {
            return;
        }

        followLatest = true;
        selectedSampleIndex = rawSamples.Count - 1;
        UpdateInspection();
    }

    private static string FormatTimeTick(DateTimeOffset value, TimeSpan duration) =>
        duration.TotalHours >= 24
            ? value.ToString("MMM d HH:mm")
            : duration.TotalMinutes <= 10 ? value.ToString("HH:mm:ss") : value.ToString("HH:mm");

    private static string FormatValue(double? value, MetricChoice choice) => value is { } number
        ? $"{number / choice.Divisor:0.##} {choice.Unit}"
        : LocalizedResources.Get("TelemetryUI_Unavailable", "Unavailable");
}
