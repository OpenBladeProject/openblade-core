using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;
using Brush = Microsoft.UI.Xaml.Media.Brush;
using Button = Microsoft.UI.Xaml.Controls.Button;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Point = Windows.Foundation.Point;
using Size = Windows.Foundation.Size;

namespace OpenBlade.Tray;

/// <summary>Synchronized unit lanes and an analysis table backed by original samples.</summary>
public sealed partial class TelemetryChart : Grid
{
    private const double PlotTop = 10;
    private const double PlotRight = 12;
    private readonly AccessibilitySettings accessibility = new();

    public static readonly DependencyProperty PlotStrokeProperty = DependencyProperty.Register(
        nameof(PlotStroke), typeof(Brush), typeof(TelemetryChart),
        new PropertyMetadata(null, (sender, _) => ((TelemetryChart)sender).Draw()));
    public static readonly DependencyProperty GridStrokeProperty = DependencyProperty.Register(
        nameof(GridStroke), typeof(Brush), typeof(TelemetryChart),
        new PropertyMetadata(null, (sender, _) => ((TelemetryChart)sender).Draw()));

    private sealed record MetricChoice(
        TelemetryMetric Metric, string Label, string Unit, double Divisor,
        string LightColor, string DarkColor, bool Dashed = false);

    private sealed class Lane(MetricChoice[] metrics, TextBlock title)
    {
        public MetricChoice[] Metrics { get; } = metrics;
        public StackPanel Root { get; } = new() { Spacing = 2 };
        public TextBlock Title { get; } = title;
        public Canvas Plot { get; } = new()
        {
            Height = 112,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        public Line Crosshair { get; } = new() { StrokeThickness = 1, StrokeDashArray = [2, 3] };
        public Dictionary<TelemetryMetric, Ellipse> Markers { get; } = [];
        public Border Readout { get; } = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        public TextBlock ReadoutTime { get; } = Text(AppTypography.Caption);
        public Dictionary<TelemetryMetric, TextBlock> ReadoutValues { get; } = [];
        public TelemetryChartScale? Scale { get; set; }
        public double AreaWidth { get; set; }
        public double AreaHeight { get; set; }
    }

    private readonly MetricChoice[] choices;
    private readonly Lane[] lanes;
    private readonly TextBlock inspectionState = Text(AppTypography.BodyStrong);
    private readonly TextBlock inspectionMode = Text(AppTypography.Caption);
    private readonly TextBlock noReadings = Text(AppTypography.Caption,
        LocalizedResources.Get("TelemetryUI_WaitingForReadings", "Waiting for readings"));
    private readonly Button previousSampleButton;
    private readonly Button nextSampleButton;
    private readonly Button followLatestButton;
    private readonly ContentWrapPanel sampleCommands = new();
    private readonly StackPanel panel = new() { Spacing = 12 };
    private IReadOnlyList<TelemetryChartMetricPreference> metricPreferences =
        TelemetryChartPreferenceDefaults.Normalize(null);
    private TelemetryHistorySnapshot? retainedSnapshot;
    private TelemetryHistorySnapshot? snapshot;
    private IReadOnlyList<TelemetrySample> rawSamples = [];
    private TelemetryTimeRangeSelection? timeRange;
    private Guid? sourceSessionId;
    private int selectedSampleIndex = -1;
    private bool followLatest = true;
    private bool samplePinned;
    private HistorySelectionNotice historySelectionNotice;

    private enum HistorySelectionNotice { None, RangeAdjusted, SampleExpired }
    private bool updatingPreferences;
    private bool viewportDrawQueued;
    private double plotLeft = 64;
    private double plotBottom = 28;
#if OPENBLADE_DEVELOPER_HARNESS
    private double? localizationSnapshotInspectionFraction;
    private bool localizationSnapshotFocusNextSample;
#endif

    public TelemetryChart()
    {
        choices =
        [
            new(TelemetryMetric.CpuTemperatureCelsius,
                LocalizedResources.Get("TelemetryUI_CpuTemperature", "CPU temperature"), "°C", 1, "#278524", "#83D970"),
            new(TelemetryMetric.GpuTemperatureCelsius,
                LocalizedResources.Get("TelemetryUI_GpuTemperature", "GPU temperature"), "°C", 1, "#187CB5", "#73BFFA", true),
            new(TelemetryMetric.BatteryPowerFlowMilliwatts,
                LocalizedResources.Get("TelemetryUI_BatteryPower", "Battery power"), "W", 1000, "#996317", "#E3AD61"),
            new(TelemetryMetric.Fan1Rpm,
                LocalizedResources.Get("TelemetryUI_Fan1Speed", "Fan 1 speed"), "RPM", 1, "#7D4ACA", "#C0A3F1"),
            new(TelemetryMetric.Fan2Rpm,
                LocalizedResources.Get("TelemetryUI_Fan2Speed", "Fan 2 speed"), "RPM", 1, "#B13D68", "#EF9EB8", true),
        ];
        previousSampleButton = Command(LocalizedResources.Get("TelemetryUI_PreviousSample", "Previous sample"));
        nextSampleButton = Command(LocalizedResources.Get("TelemetryUI_NextSample", "Next sample"));
        followLatestButton = Command(LocalizedResources.Get("TelemetryUI_FollowLatest", "Follow latest"));
        (Button metricEditor, UIElement metricLegend) = CreateMetricEditor();
        sampleCommands.Children.Add(previousSampleButton);
        sampleCommands.Children.Add(nextSampleButton);
        sampleCommands.Children.Add(followLatestButton);
        sampleCommands.Children.Add(metricEditor);
        panel.Children.Add(inspectionState);
        panel.Children.Add(inspectionMode);
        panel.Children.Add(sampleCommands);
        panel.Children.Add(metricLegend);
        lanes =
        [
            new([choices[0], choices[1]], Text(AppTypography.BodyStrong,
                LocalizedResources.Get("TelemetryUI_Temperatures", "Temperatures (°C)"))),
            new([choices[2]], Text(AppTypography.BodyStrong,
                LocalizedResources.Get("TelemetryUI_PowerLane", "Battery power (W)"))),
            new([choices[3], choices[4]], Text(AppTypography.BodyStrong,
                LocalizedResources.Get("TelemetryUI_FanLane", "Fan speed (RPM)"))),
        ];
        foreach (Lane lane in lanes)
        {
            lane.Root.Children.Add(lane.Title);
            lane.Root.Children.Add(lane.Plot);
            foreach (MetricChoice choice in lane.Metrics)
            {
                lane.Markers.Add(choice.Metric, new Ellipse { Width = 7, Height = 7 });
            }
            CreateLaneReadout(lane);
            lane.Plot.SizeChanged += (_, _) => Draw();
            lane.Plot.PointerMoved += (_, args) => OnPlotPointerMoved(lane, args);
            lane.Plot.PointerPressed += (_, args) => OnPlotPointerPressed(lane, args);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(lane.Plot,
                LocalizedResources.Get("TelemetryUI_ChartAutomationName",
                    "Telemetry timeline. Point to the chart or use the sample buttons for exact readings."));
            panel.Children.Add(lane.Root);
        }
        panel.Children.Add(noReadings);
        panel.Children.Add(CreateRangeEditor());
        panel.Children.Add(CreateAnalysisTable());
        Children.Add(panel);
        previousSampleButton.Click += (_, _) => MoveSelection(-1);
        nextSampleButton.Click += (_, _) => MoveSelection(1);
        followLatestButton.Click += (_, _) => FollowLatest();
        SizeChanged += (_, _) => UpdateTableLayout();
        ActualThemeChanged += (_, _) => Draw();
        Loaded += (_, _) => Draw();
    }

    public event EventHandler? PreferencesChanged;
    public event EventHandler? VisibleRangeChanged;

    public IReadOnlyList<TelemetryChartMetricPreference> MetricPreferences => metricPreferences.ToArray();
    public IReadOnlyList<TelemetryEvent> VisibleEvents => snapshot?.Events ?? [];

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

    public void ApplyPreferences(IReadOnlyList<TelemetryChartMetricPreference> preferences)
    {
        metricPreferences = TelemetryChartPreferenceDefaults.Normalize(preferences);
        RefreshMetricEditor();
        Draw();
    }

    public void Update(
        TelemetryHistorySnapshot value,
        DateTimeOffset? visibleStartUtc = null,
        DateTimeOffset? visibleEndUtc = null,
        Guid? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (visibleStartUtc.HasValue != visibleEndUtc.HasValue || visibleStartUtc > visibleEndUtc)
        {
            throw new ArgumentException("Both ordered telemetry time bounds must be supplied together.");
        }
        DateTimeOffset end = visibleEndUtc ?? value.RangeEndUtc ?? DateTimeOffset.UtcNow;
        DateTimeOffset start = visibleStartUtc ?? value.RangeStartUtc ?? end;
        bool reset = sourceSessionId != sessionId;
        sourceSessionId = sessionId;
        retainedSnapshot = value;
        TelemetryTimeRangeSelection? previousRange = timeRange;
        if (reset)
        {
            rangeDragBaseline = null;
            overview.ReleasePointerCaptures();
            showAllRetained = true;
        }
        // Dragging owns a fixed baseline until capture ends; a sample update may not move it.
        if (rangeDragBaseline is null)
        {
            timeRange = timeRange is null || showAllRetained
                ? TelemetryTimeRangeSelection.Create(start, end)
                : timeRange.UpdateSource(start, end);
            if (!reset && previousRange is { FollowLatest: false }
                && (timeRange.StartUtc != previousRange.StartUtc || timeRange.EndUtc != previousRange.EndUtc)
                && historySelectionNotice != HistorySelectionNotice.SampleExpired)
            {
                historySelectionNotice = HistorySelectionNotice.RangeAdjusted;
            }
        }
        if (reset)
        {
            samplePinned = false;
            followLatest = true;
            historySelectionNotice = HistorySelectionNotice.None;
            rangeValidation.Visibility = Visibility.Collapsed;
        }
        RebuildViewport();
    }

    private static TextBlock Text(string role, string? content = null)
    {
        var text = new TextBlock { Text = content ?? string.Empty, TextWrapping = TextWrapping.Wrap };
        AppTypography.Apply(text, role);
        return text;
    }

    private static Button Command(string content)
    {
        var button = new Button
        {
            Content = content,
            Style = AppTypography.RequireStyle(AppTypography.CommandButton),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, content);
        return button;
    }

    private static Grid Columns(params FrameworkElement[] children)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        foreach (FrameworkElement child in children)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(child, grid.Children.Count);
            grid.Children.Add(child);
        }
        return grid;
    }

    private void RebuildViewport()
    {
        if (retainedSnapshot is null || timeRange is null)
        {
            return;
        }
        DateTimeOffset? selected = SelectedSample?.TimestampUtc;
        snapshot = TelemetryChartViewport.BuildSnapshot(retainedSnapshot, timeRange.StartUtc, timeRange.EndUtc);
        rawSamples = snapshot.RawSamples;
        if (samplePinned && selected is { } pinned
            && !rawSamples.Any(sample => sample.TimestampUtc == pinned))
        {
            samplePinned = false;
            followLatest = false;
            historySelectionNotice = HistorySelectionNotice.SampleExpired;
        }
        selectedSampleIndex = rawSamples.Count == 0 || historySelectionNotice == HistorySelectionNotice.SampleExpired ? -1
            : followLatest ? rawSamples.Count - 1
            : selected is { } target ? TelemetryChartSelection.FindNearestSampleIndex(rawSamples, target) : 0;
        Draw();
        VisibleRangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Draw()
    {
        if (snapshot is null || lanes is null)
        {
            return;
        }
        TextBlock measurement = Text(AppTypography.Caption, "−20,000");
        measurement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        plotLeft = Math.Max(64, measurement.DesiredSize.Width + 12);
        plotBottom = measurement.DesiredSize.Height + 12;
        foreach (Lane lane in lanes)
        {
            PrepareLaneReadout(lane, SelectedSample);
            lane.Plot.Height = Math.Max(Math.Max(112, measurement.DesiredSize.Height * 6),
                lane.Readout.DesiredSize.Height + PlotTop + plotBottom + 8);
            DrawLane(lane);
        }
        noReadings.Visibility = rawSamples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshMetricEditor();
        UpdateRangeEditor();
        UpdateAnalysisStatistics();
        UpdateInspection();
#if OPENBLADE_DEVELOPER_HARNESS
        ApplyLocalizationSnapshotInspection();
#endif
    }

    private void DrawLane(Lane lane)
    {
        lane.Plot.Children.Clear();
        lane.Scale = null;
        MetricChoice[] visible = lane.Metrics.Where(IsMetricVisible).ToArray();
        lane.Root.Visibility = visible.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visible.Length == 0 || snapshot is null)
        {
            return;
        }
        double width = lane.Plot.ActualWidth;
        lane.AreaWidth = width - plotLeft - PlotRight;
        lane.AreaHeight = lane.Plot.Height - PlotTop - plotBottom;
        if (lane.AreaWidth <= 0 || lane.AreaHeight <= 0 || timeRange is null || GridStroke is not { } gridBrush)
        {
            return;
        }
        TelemetrySeriesSnapshot[] series = snapshot.Series
            .Where(item => visible.Any(choice => choice.Metric == item.Metric)).ToArray();
        double[] minima = series.Select(item => item.Statistics.Minimum).OfType<double>().ToArray();
        double[] maxima = series.Select(item => item.Statistics.Maximum).OfType<double>().ToArray();
        lane.Scale = TelemetryChartScale.Create(timeRange.StartUtc, timeRange.EndUtc,
            minima.Length > 0 ? minima.Min() : 0, maxima.Length > 0 ? maxima.Max() : 1);
        double divisor = visible[0].Divisor;
        if (minima.Length > 0)
        {
            foreach (double tick in lane.Scale.CreateValueTicks(3))
            {
                double y = PlotTop + lane.Scale.ValueFractionFromTop(tick) * lane.AreaHeight;
                lane.Plot.Children.Add(new Line { X1 = plotLeft, X2 = width - PlotRight, Y1 = y, Y2 = y, Stroke = gridBrush });
                TextBlock label = Text(AppTypography.Caption, (tick / divisor).ToString("0.#"));
                label.TextAlignment = TextAlignment.Right;
                label.Width = plotLeft - 10;
                label.Measure(new Size(label.Width, double.PositiveInfinity));
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
                lane.Plot.Children.Add(label);
            }
        }
        int tickCount = lane.AreaWidth >= 520 ? 5 : 3;
        TimeSpan duration = timeRange.Duration;
        foreach (DateTimeOffset tick in lane.Scale.CreateTimeTicks(tickCount))
        {
            double x = plotLeft + lane.Scale.TimeFraction(tick) * lane.AreaWidth;
            TextBlock label = Text(AppTypography.Caption, FormatTimeTick(PresentationTimestamp.ForDisplay(tick), duration));
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, plotLeft,
                Math.Max(plotLeft, width - PlotRight - label.DesiredSize.Width)));
            Canvas.SetTop(label, PlotTop + lane.AreaHeight + 6);
            lane.Plot.Children.Add(label);
        }
        foreach (MetricChoice choice in visible)
        {
            Brush brush = SeriesBrush(choice);
            TelemetrySeriesSnapshot? data = series.FirstOrDefault(item => item.Metric == choice.Metric);
            Polyline? segment = null;
            foreach (TelemetryChartPoint point in data?.Points ?? [])
            {
                if (point.Value is not { } value)
                {
                    DrawIsolatedPoint(lane.Plot, segment, brush);
                    segment = null;
                    continue;
                }
                if (segment is null)
                {
                    segment = new Polyline { Stroke = brush, StrokeThickness = 2 };
                    if (choice.Dashed)
                    {
                        segment.StrokeDashArray = [4, 3];
                    }

                    lane.Plot.Children.Add(segment);
                }
                segment.Points.Add(new Point(plotLeft + lane.Scale.TimeFraction(point.TimestampUtc) * lane.AreaWidth,
                    PlotTop + lane.Scale.ValueFractionFromTop(value) * lane.AreaHeight));
            }
            DrawIsolatedPoint(lane.Plot, segment, brush);
            Ellipse marker = lane.Markers[choice.Metric];
            marker.Fill = brush;
            lane.Plot.Children.Add(marker);
        }
        foreach (TelemetryEvent marker in snapshot.Events.TakeLast(64))
        {
            double x = plotLeft + lane.Scale.TimeFraction(marker.TimestampUtc) * lane.AreaWidth;
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = PlotTop,
                Y2 = PlotTop + lane.AreaHeight,
                Stroke = gridBrush,
                StrokeDashArray = [2, 3]
            };
            ToolTipService.SetToolTip(line, $"{PresentationTimestamp.ForDisplay(marker.TimestampUtc):G} · {marker.PreviousValue} → {marker.CurrentValue}");
            lane.Plot.Children.Add(line);
        }
        lane.Crosshair.Stroke = inspectionState.Foreground;
        lane.Plot.Children.Add(lane.Crosshair);
        lane.Plot.Children.Add(lane.Readout);
    }

    private static void DrawIsolatedPoint(Canvas canvas, Polyline? segment, Brush brush)
    {
        if (segment?.Points.Count != 1)
        {
            return;
        }
        Point point = segment.Points[0];
        var dot = new Ellipse { Width = 4, Height = 4, Fill = brush };
        Canvas.SetLeft(dot, point.X - 2);
        Canvas.SetTop(dot, point.Y - 2);
        canvas.Children.Add(dot);
    }

    private TelemetrySample? SelectedSample => selectedSampleIndex >= 0 && selectedSampleIndex < rawSamples.Count
        ? rawSamples[selectedSampleIndex] : null;

    private void UpdateInspection()
    {
        TelemetrySample? sample = SelectedSample;
        previousSampleButton.IsEnabled = selectedSampleIndex > 0;
        nextSampleButton.IsEnabled = rawSamples.Count > 0 && selectedSampleIndex < rawSamples.Count - 1;
        followLatestButton.IsEnabled = retainedSnapshot?.RawSamples.Count > 0 && (!followLatest || timeRange?.FollowLatest != true);
        inspectionState.Text = sample is null
            ? historySelectionNotice == HistorySelectionNotice.SampleExpired
                ? LocalizedResources.Get("TelemetryUI_SelectedReadingExpired", "Selected reading is no longer kept.")
                : LocalizedResources.Get("TelemetryUI_NoSampleSelected", "No sample selected.")
            : LocalizedResources.Format("TelemetryUI_SelectedMoment", "Selected moment · {0}", PresentationTimestamp.ForDisplay(sample.TimestampUtc).ToString("G"));
        inspectionMode.Text = followLatest
            ? LocalizedResources.Get("TelemetryUI_Following", "Following latest")
            : samplePinned ? LocalizedResources.Get("TelemetryUI_Pinned", "Pinned")
            : LocalizedResources.Get("TelemetryUI_InspectHint", "Hover to inspect. Click to pin a sample.");
        if (historySelectionNotice == HistorySelectionNotice.RangeAdjusted)
        {
            inspectionMode.Text += " · " + LocalizedResources.Get("TelemetryUI_RangeHistoryExpired",
                "Older readings are no longer kept. The visible time range was adjusted.");
        }
        tableMomentText.Text = $"{inspectionState.Text} · {inspectionMode.Text}";
        UpdateAnalysisValues(sample);
        foreach (Lane lane in lanes)
        {
            lane.Crosshair.Visibility = sample is not null && lane.Scale is not null ? Visibility.Visible : Visibility.Collapsed;
            lane.Readout.Visibility = lane.Crosshair.Visibility;
            if (sample is null || lane.Scale is null)
            {
                foreach (Ellipse marker in lane.Markers.Values)
                {
                    marker.Visibility = Visibility.Collapsed;
                }
                continue;
            }

            double x = plotLeft + lane.Scale.TimeFraction(sample.TimestampUtc) * lane.AreaWidth;
            lane.Crosshair.X1 = x;
            lane.Crosshair.X2 = x;
            lane.Crosshair.Y1 = PlotTop;
            lane.Crosshair.Y2 = PlotTop + lane.AreaHeight;
            UpdateLaneReadout(lane, sample, x);
            foreach (MetricChoice choice in lane.Metrics)
            {
                Ellipse marker = lane.Markers[choice.Metric];
                double? value = sample.Value(choice.Metric);
                marker.Visibility = value is not null && IsMetricVisible(choice) ? Visibility.Visible : Visibility.Collapsed;
                if (value is { } number)
                {
                    Canvas.SetLeft(marker, x - marker.Width / 2);
                    Canvas.SetTop(marker, PlotTop + lane.Scale.ValueFractionFromTop(number) * lane.AreaHeight - marker.Height / 2);
                }
            }
        }
    }

    private void OnPlotPointerMoved(Lane lane, PointerRoutedEventArgs args)
    {
        if (!samplePinned)
        {
            InspectAtPosition(lane, args.GetCurrentPoint(lane.Plot).Position.X);
        }
    }

    private void OnPlotPointerPressed(Lane lane, PointerRoutedEventArgs args)
    {
        if (!InspectAtPosition(lane, args.GetCurrentPoint(lane.Plot).Position.X))
        {
            return;
        }
        PinSelection();
        args.Handled = true;
    }

    private bool InspectAtPosition(Lane lane, double x)
    {
        if (lane.Scale is null || rawSamples.Count == 0 || lane.AreaWidth <= 0)
        {
            return false;
        }

        InspectAtFraction((x - plotLeft) / lane.AreaWidth);
        return true;
    }

    private void InspectAtFraction(double fraction)
    {
        if (timeRange is null || rawSamples.Count == 0)
        {
            return;
        }

        DateTimeOffset target = TelemetryChartScale.Create(timeRange.StartUtc, timeRange.EndUtc, 0, 1)
            .TimestampAtFraction(fraction);
        int nearest = TelemetryChartSelection.FindNearestSampleIndex(rawSamples, target);
        if (nearest == selectedSampleIndex && !followLatest && historySelectionNotice == HistorySelectionNotice.None)
        {
            return;
        }
        historySelectionNotice = HistorySelectionNotice.None;
        selectedSampleIndex = nearest;
        followLatest = false;
        UpdateInspection();
    }

    private void MoveSelection(int offset)
    {
        if (rawSamples.Count == 0)
        {
            return;
        }

        historySelectionNotice = HistorySelectionNotice.None;
        selectedSampleIndex = Math.Clamp(selectedSampleIndex + offset, 0, rawSamples.Count - 1);
        PinSelection();
    }

    private void PinSelection()
    {
        if (SelectedSample is null)
        {
            return;
        }
        historySelectionNotice = HistorySelectionNotice.None;
        followLatest = false;
        samplePinned = true;
        showAllRetained = false;
        timeRange = timeRange?.SetFollowLatest(false);
        UpdateInspection();
    }

    private void FollowLatest()
    {
        historySelectionNotice = HistorySelectionNotice.None;
        timeRange = timeRange?.SetFollowLatest(true);
        followLatest = true;
        samplePinned = false;
        RebuildViewport();
    }

    private bool IsMetricVisible(MetricChoice choice) => metricPreferences.Single(value => value.Metric == choice.Metric).IsVisible;

    private Brush SeriesBrush(MetricChoice choice) => accessibility.HighContrast
        ? inspectionState.Foreground
        : new SolidColorBrush(ParseColor(metricPreferences.Single(value => value.Metric == choice.Metric).ColorHex
            ?? (ActualTheme == ElementTheme.Dark ? choice.DarkColor : choice.LightColor)));

    private static Windows.UI.Color ParseColor(string value) => Windows.UI.Color.FromArgb(255,
        Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16));

    private static string FormatTimeTick(DateTimeOffset value, TimeSpan duration) => duration.TotalHours >= 24
        ? value.ToString("MMM d HH:mm") : duration.TotalMinutes <= 10 ? value.ToString("HH:mm:ss") : value.ToString("HH:mm");

    private static string FormatNumber(double? value, MetricChoice choice) => value is { } number
        ? (number / choice.Divisor).ToString(choice.Unit == "RPM" ? "N0" : "0.##") : "—";

#if OPENBLADE_DEVELOPER_HARNESS
    internal void ShowLocalizationSnapshotInspection(double fraction, bool focusNextSampleButton = false)
    {
        localizationSnapshotInspectionFraction = Math.Clamp(fraction, 0, 1);
        localizationSnapshotFocusNextSample = focusNextSampleButton;
        ApplyLocalizationSnapshotInspection();
    }

    private void ApplyLocalizationSnapshotInspection()
    {
        if (timeRange is null || localizationSnapshotInspectionFraction is not { } fraction)
        {
            return;
        }
        localizationSnapshotInspectionFraction = null;
        InspectAtFraction(fraction);
        PinSelection();
        if (localizationSnapshotFocusNextSample)
        {
            _ = nextSampleButton.Focus(FocusState.Keyboard);
        }
        localizationSnapshotFocusNextSample = false;
    }
#endif
}
