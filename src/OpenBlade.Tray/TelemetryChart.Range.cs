using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Brush = Microsoft.UI.Xaml.Media.Brush;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Point = Windows.Foundation.Point;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace OpenBlade.Tray;

public sealed partial class TelemetryChart
{
    private readonly Canvas overview = new()
    {
        Height = 76,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private readonly TextBox rangeStartInput = new()
    {
        Header = LocalizedResources.Get("TelemetryUI_RangeStart", "Start"),
        MinWidth = 0,
    };
    private readonly TextBox rangeEndInput = new()
    {
        Header = LocalizedResources.Get("TelemetryUI_RangeEnd", "End"),
        MinWidth = 0,
    };
    private readonly TextBlock rangeValidation = Text(AppTypography.Caption);
    private readonly TextBlock overviewLabel = Text(AppTypography.Caption,
        LocalizedResources.Get("TelemetryUI_VisibleTimeRange", "Visible time range"));
    private TelemetryTimeRangeSelection? rangeDragBaseline;
    private const string RangeTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz";
    private string displayedRangeStart = string.Empty;
    private string displayedRangeEnd = string.Empty;
    private double rangeDragOriginX;
    private RangeDragKind rangeDragKind;
    private bool showAllRetained = true;

    private enum RangeDragKind { Start, End, Pan }

    private UIElement CreateRangeEditor()
    {
        var editor = new StackPanel { Spacing = 8 };
        editor.Children.Add(overviewLabel);
        editor.Children.Add(overview);
        editor.Children.Add(Columns(rangeStartInput, rangeEndInput));
        var fullRange = Command(LocalizedResources.Get("TelemetryUI_FullRange", "Full range"));
        fullRange.Click += (_, _) =>
        {
            if (timeRange is null)
            {
                return;
            }

            historySelectionNotice = HistorySelectionNotice.None;
            showAllRetained = true;
            timeRange = TelemetryTimeRangeSelection.Create(timeRange.SourceStartUtc, timeRange.SourceEndUtc);
            followLatest = true;
            samplePinned = false;
            RebuildViewport();
        };
        editor.Children.Add(fullRange);
        editor.Children.Add(Text(AppTypography.Caption,
            LocalizedResources.Get("TelemetryUI_RangeHint", "Drag either edge to resize, or drag the selected window to move it. You can also edit the start and end times.")));
        editor.Children.Add(rangeValidation);
        rangeValidation.Visibility = Visibility.Collapsed;
        overview.SizeChanged += (_, _) => DrawOverview();
        overview.PointerPressed += OnRangePointerPressed;
        overview.PointerMoved += OnRangePointerMoved;
        overview.PointerReleased += OnRangePointerReleased;
        overview.PointerCanceled += OnRangePointerReleased;
        overview.PointerCaptureLost += (_, _) => EndRangeDrag();
        foreach (TextBox input in new[] { rangeStartInput, rangeEndInput })
        {
            input.LostFocus += (_, _) => CommitRangeTime(input);
            input.KeyDown += (_, args) =>
            {
                if (args.Key == VirtualKey.Enter)
                {
                    CommitRangeTime(input);
                    args.Handled = true;
                }
            };
        }
        KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && samplePinned)
            {
                samplePinned = false;
                UpdateInspection();
                args.Handled = true;
            }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(overview,
            LocalizedResources.Get("TelemetryUI_RangeAutomation", "Visible time range. Drag its edges or edit the start and end time fields."));
        return editor;
    }

    private void UpdateRangeEditor()
    {
        if (timeRange is null)
        {
            return;
        }

        if (rangeStartInput.FocusState == FocusState.Unfocused)
        {
            rangeStartInput.Text = displayedRangeStart = FormatRangeTime(timeRange.StartUtc);
        }

        if (rangeEndInput.FocusState == FocusState.Unfocused)
        {
            rangeEndInput.Text = displayedRangeEnd = FormatRangeTime(timeRange.EndUtc);
        }

        rangeStartInput.IsEnabled = rangeEndInput.IsEnabled = retainedSnapshot?.RawSamples.Count > 0;
        DrawOverview();
    }

    private void CommitRangeTime(TextBox input)
    {
        if (timeRange is null || !input.IsEnabled)
        {
            return;
        }

        bool isStart = ReferenceEquals(input, rangeStartInput);
        if (string.Equals(input.Text, isStart ? displayedRangeStart : displayedRangeEnd, StringComparison.Ordinal))
        {
            return;
        }
        if (!DateTimeOffset.TryParseExact(input.Text, RangeTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset parsed)
            && !DateTimeOffset.TryParse(input.Text, CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal, out parsed))
        {
            rangeValidation.Text = LocalizedResources.Get("TelemetryUI_InvalidRangeTime", "Enter a valid date and time.");
            rangeValidation.Visibility = Visibility.Visible;
            return;
        }
        DateTimeOffset requested = parsed.ToUniversalTime();
        SelectTimeRange(isStart
            ? timeRange.MoveStart(requested, TimeSpan.FromSeconds(1))
            : timeRange.MoveEnd(requested, TimeSpan.FromSeconds(1)));
        DateTimeOffset applied = isStart ? timeRange.StartUtc : timeRange.EndUtc;
        rangeValidation.Text = LocalizedResources.Get("TelemetryUI_RangeTimeAdjusted",
            "Adjusted to the available time range.");
        rangeValidation.Visibility = applied == requested ? Visibility.Collapsed : Visibility.Visible;
        string formatted = FormatRangeTime(isStart ? timeRange.StartUtc : timeRange.EndUtc);
        input.Text = formatted;
        if (isStart)
        {
            displayedRangeStart = formatted;
        }
        else
        {
            displayedRangeEnd = formatted;
        }
        RebuildViewport();
    }

    private void SelectTimeRange(TelemetryTimeRangeSelection selection)
    {
        timeRange = selection.SetFollowLatest(false);
        showAllRetained = false;
        followLatest = false;
        samplePinned = false;
        historySelectionNotice = HistorySelectionNotice.None;
        rangeValidation.Visibility = Visibility.Collapsed;
    }

    private static string FormatRangeTime(DateTimeOffset value) =>
        PresentationTimestamp.ForDisplay(value).ToString(RangeTimeFormat, CultureInfo.InvariantCulture);

    private void DrawOverview()
    {
        overview.Children.Clear();
        if (timeRange is null || retainedSnapshot is null || overview.ActualWidth < 24)
        {
            return;
        }

        double width = overview.ActualWidth - 16;
        TelemetryChartScale domain = TelemetryChartScale.Create(timeRange.SourceStartUtc, timeRange.SourceEndUtc, 0, 1);
        Brush neutral = GridStroke ?? inspectionState.Foreground;
        Brush accent = PlotStroke ?? inspectionState.Foreground;
        MetricChoice? choice = choices.FirstOrDefault(candidate => IsMetricVisible(candidate)
            && retainedSnapshot.Series.Any(series => series.Metric == candidate.Metric && series.Statistics.Count > 0));
        overviewLabel.Text = LocalizedResources.Get("TelemetryUI_VisibleTimeRange", "Visible time range");
        if (choice is not null)
        {
            TelemetrySeriesSnapshot series = retainedSnapshot.Series.Single(value => value.Metric == choice.Metric);
            TelemetryChartScale values = TelemetryChartScale.Create(timeRange.SourceStartUtc, timeRange.SourceEndUtc,
                series.Statistics.Minimum!.Value, series.Statistics.Maximum!.Value);
            Polyline? segment = null;
            foreach (TelemetryChartPoint point in series.Points)
            {
                if (point.Value is not { } value) { segment = null; continue; }
                if (segment is null)
                {
                    segment = new Polyline { Stroke = neutral, StrokeThickness = 1.5 };
                    overview.Children.Add(segment);
                }
                segment.Points.Add(new Point(8 + domain.TimeFraction(point.TimestampUtc) * width,
                    8 + values.ValueFractionFromTop(value) * 32));
            }
            overviewLabel.Text = LocalizedResources.Format("TelemetryUI_RangeOverview", "Visible time range · {0}", choice.Label);
        }
        double left = 8 + domain.TimeFraction(timeRange.StartUtc) * width;
        double right = 8 + domain.TimeFraction(timeRange.EndUtc) * width;
        var selection = new Rectangle
        {
            Width = Math.Max(2, right - left),
            Height = 44,
            Fill = accent,
            Opacity = 0.16,
        };
        Canvas.SetLeft(selection, left);
        overview.Children.Add(selection);
        foreach (double edge in new[] { left, right })
        {
            var handle = new Rectangle { Width = 4, Height = 44, RadiusX = 2, RadiusY = 2, Fill = accent };
            Canvas.SetLeft(handle, Math.Clamp(edge - 2, 0, overview.ActualWidth - 4));
            overview.Children.Add(handle);
        }
        foreach (DateTimeOffset tick in domain.CreateTimeTicks(3))
        {
            TextBlock label = Text(AppTypography.Caption, FormatTimeTick(PresentationTimestamp.ForDisplay(tick),
                timeRange.SourceEndUtc - timeRange.SourceStartUtc));
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            overview.Height = Math.Max(76, 56 + label.DesiredSize.Height);
            Canvas.SetLeft(label, Math.Clamp(8 + domain.TimeFraction(tick) * width - label.DesiredSize.Width / 2,
                0, Math.Max(0, overview.ActualWidth - label.DesiredSize.Width)));
            Canvas.SetTop(label, 48);
            overview.Children.Add(label);
        }
    }

    private void OnRangePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (timeRange is null || retainedSnapshot?.RawSamples.Count is not > 0 || overview.ActualWidth <= 16)
        {
            return;
        }

        var point = args.GetCurrentPoint(overview);
        if (!point.IsInContact)
        {
            return;
        }

        double x = point.Position.X;
        TelemetryChartScale domain = TelemetryChartScale.Create(timeRange.SourceStartUtc, timeRange.SourceEndUtc, 0, 1);
        double left = 8 + domain.TimeFraction(timeRange.StartUtc) * (overview.ActualWidth - 16);
        double right = 8 + domain.TimeFraction(timeRange.EndUtc) * (overview.ActualWidth - 16);
        double startDistance = Math.Abs(x - left);
        double endDistance = Math.Abs(x - right);
        rangeDragKind = x > left + 14 && x < right - 14 ? RangeDragKind.Pan
            : startDistance <= endDistance ? RangeDragKind.Start : RangeDragKind.End;
        rangeDragOriginX = x;
        rangeDragBaseline = timeRange;
        if (!overview.CapturePointer(args.Pointer))
        {
            rangeDragBaseline = null;
        }

        args.Handled = true;
    }

    private void OnRangePointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (rangeDragBaseline is not { } baseline || overview.ActualWidth <= 16)
        {
            return;
        }

        double fraction = (args.GetCurrentPoint(overview).Position.X - rangeDragOriginX) / (overview.ActualWidth - 16);
        long ticks = (long)Math.Round((baseline.SourceEndUtc - baseline.SourceStartUtc).Ticks * Math.Clamp(fraction, -1, 1));
        SelectTimeRange(ApplyRangeDrag(baseline, rangeDragKind, ticks));
        if (!viewportDrawQueued)
        {
            viewportDrawQueued = true;
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                viewportDrawQueued = false;
                RebuildViewport();
            }))
            {
                viewportDrawQueued = false;
            }
        }
        args.Handled = true;
    }

    private static TelemetryTimeRangeSelection ApplyRangeDrag(
        TelemetryTimeRangeSelection baseline, RangeDragKind kind, long ticks)
    {
        DateTimeOffset ClampTarget(DateTimeOffset value) => new(Math.Clamp(
            value.Ticks + ticks, baseline.SourceStartUtc.Ticks, baseline.SourceEndUtc.Ticks), TimeSpan.Zero);
        return kind switch
        {
            RangeDragKind.Start => baseline.MoveStart(ClampTarget(baseline.StartUtc), TimeSpan.FromSeconds(1)),
            RangeDragKind.End => baseline.MoveEnd(ClampTarget(baseline.EndUtc), TimeSpan.FromSeconds(1)),
            _ => baseline.Pan(TimeSpan.FromTicks(ticks)),
        };
    }

    private void OnRangePointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (rangeDragBaseline is null)
        {
            return;
        }

        overview.ReleasePointerCapture(args.Pointer);
        EndRangeDrag();
        args.Handled = true;
    }

    private void EndRangeDrag()
    {
        if (rangeDragBaseline is null)
        {
            return;
        }

        rangeDragBaseline = null;
        RebuildViewport();
    }
}
