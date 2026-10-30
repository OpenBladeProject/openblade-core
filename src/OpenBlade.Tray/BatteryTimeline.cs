using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBlade.Session;
using Brush = Microsoft.UI.Xaml.Media.Brush;

namespace OpenBlade.Tray;

public sealed class BatteryTimeline : Grid
{
    public static readonly DependencyProperty AccentStrokeProperty = DependencyProperty.Register(
        nameof(AccentStroke), typeof(Brush), typeof(BatteryTimeline),
        new PropertyMetadata(null, (sender, _) => ((BatteryTimeline)sender).Draw()));
    public static readonly DependencyProperty UnknownStrokeProperty = DependencyProperty.Register(
        nameof(UnknownStroke), typeof(Brush), typeof(BatteryTimeline),
        new PropertyMetadata(null, (sender, _) => ((BatteryTimeline)sender).Draw()));
    public static readonly DependencyProperty SegmentBackgroundProperty = DependencyProperty.Register(
        nameof(SegmentBackground), typeof(Brush), typeof(BatteryTimeline),
        new PropertyMetadata(null, (sender, _) => ((BatteryTimeline)sender).Draw()));
    public Brush? AccentStroke
    {
        get => (Brush?)GetValue(AccentStrokeProperty);
        set => SetValue(AccentStrokeProperty, value);
    }

    public Brush? UnknownStroke
    {
        get => (Brush?)GetValue(UnknownStrokeProperty);
        set => SetValue(UnknownStrokeProperty, value);
    }

    public Brush? SegmentBackground
    {
        get => (Brush?)GetValue(SegmentBackgroundProperty);
        set => SetValue(SegmentBackgroundProperty, value);
    }

    private readonly Canvas[] lanes = [new(), new(), new()];
    private readonly Grid axis = new();
    private readonly TextBlock startLabel = new();
    private readonly TextBlock endLabel = new();
    private readonly Canvas ticks = new();
    private WindowsBatteryReport? report;
    private IReadOnlyList<WindowsBatteryReportInterval> intervals = [];

    public BatteryTimeline()
    {
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 140 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] labels =
        [
            LocalizedResources.Get("BatteryTimeline_Battery", "Battery"),
            LocalizedResources.Get("BatteryTimeline_AC", "AC power"),
            LocalizedResources.Get("BatteryTimeline_Unknown", "Unknown"),
        ];
        for (int row = 0; row < lanes.Length; row++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 28 });
            var label = new TextBlock
            {
                Text = labels[row],
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            AppTypography.Apply(label, AppTypography.Caption);
            SetRow(label, row);
            Children.Add(label);
            Canvas lane = lanes[row];
            lane.Height = 18;
            lane.VerticalAlignment = VerticalAlignment.Center;
            SetRow(lane, row);
            SetColumn(lane, 1);
            Children.Add(lane);
        }
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AppTypography.Apply(startLabel, AppTypography.Caption);
        AppTypography.Apply(endLabel, AppTypography.Caption);
        startLabel.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
        endLabel.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right;
        endLabel.TextAlignment = TextAlignment.Right;
        startLabel.TextWrapping = endLabel.TextWrapping = TextWrapping.Wrap;
        axis.Margin = new Thickness(0, 4, 0, 0);
        axis.Children.Add(ticks);
        axis.Children.Add(startLabel);
        axis.Children.Add(endLabel);
        SetRow(axis, 3);
        SetColumn(axis, 1);
        Children.Add(axis);
        axis.SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    public void Update(WindowsBatteryReport? value, IReadOnlyList<WindowsBatteryReportInterval> effectiveIntervals)
    {
        report = value;
        intervals = effectiveIntervals;
        Draw();
    }

    private void Draw()
    {
        foreach (Canvas lane in lanes)
        {
            lane.Children.Clear();
        }

        ticks.Children.Clear();
        startLabel.Text = endLabel.Text = string.Empty;
        if (report is null || axis.ActualWidth <= 0)
        {
            return;
        }

        double duration = (report.WindowEndUtc - report.WindowStartUtc).TotalSeconds;
        if (duration <= 0 || AccentStroke is not { } accent || UnknownStroke is not { } neutral
            || SegmentBackground is not { } background)
        {
            return;
        }

        DateTimeOffset start = PresentationTimestamp.ForDisplay(report.WindowStartUtc);
        DateTimeOffset end = PresentationTimestamp.ForDisplay(report.WindowEndUtc);
        startLabel.MaxWidth = endLabel.MaxWidth = Math.Max(1, axis.ActualWidth / 2 - 8);
        startLabel.Text = $"{start:t}\n{start:d}";
        endLabel.Text = $"{end:t}\n{end:d}";
        startLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        endLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        // Endpoint dates reserve space before optional intermediate clock labels.
        double previousRight = startLabel.DesiredSize.Width + 16;
        for (int tick = 1; tick < 4; tick++)
        {
            var label = new TextBlock { Text = PresentationTimestamp.ForDisplay(report.WindowStartUtc.AddSeconds(duration * tick / 4)).ToString("t") };
            AppTypography.Apply(label, AppTypography.Caption);
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = axis.ActualWidth * tick / 4 - label.DesiredSize.Width / 2;
            double right = left + label.DesiredSize.Width;
            if (left < previousRight || right + 16 > axis.ActualWidth - endLabel.DesiredSize.Width)
            {
                continue;
            }

            Canvas.SetLeft(label, left);
            ticks.Children.Add(label);
            previousRight = right + 16;
        }
        foreach (WindowsBatteryReportInterval interval in intervals)
        {
            double startSeconds = Math.Clamp((interval.StartUtc - report.WindowStartUtc).TotalSeconds, 0, duration);
            double endSeconds = Math.Clamp((interval.EndUtc - report.WindowStartUtc).TotalSeconds, 0, duration);
            if (endSeconds <= startSeconds)
            {
                continue;
            }

            int row = interval.PowerSource switch
            {
                WindowsBatteryPowerSource.Battery => 0,
                WindowsBatteryPowerSource.Ac => 1,
                _ => 2,
            };
            double left = startSeconds / duration * axis.ActualWidth;
            var segment = new Border
            {
                Width = (endSeconds - startSeconds) / duration * axis.ActualWidth,
                Height = 18,
                Background = row == 2 ? background : accent,
                BorderBrush = row == 2 ? neutral : accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
            };
            string source = row switch
            {
                0 => LocalizedResources.Get("BatteryTimeline_Battery", "Battery"),
                1 => LocalizedResources.Get("BatteryTimeline_AC", "AC power"),
                _ => LocalizedResources.Get("BatteryTimeline_Unknown", "Unknown"),
            };
            string description = $"{PresentationTimestamp.ForDisplay(interval.StartUtc):g} – {PresentationTimestamp.ForDisplay(interval.EndUtc):g}: {source}";
            ToolTipService.SetToolTip(segment, description);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(segment, description);
            Canvas.SetLeft(segment, left);
            lanes[row].Children.Add(segment);
        }
    }

#if OPENBLADE_DEVELOPER_HARNESS
    internal object[] SnapshotSegments() => lanes.SelectMany(lane => lane.Children.Cast<object>()).ToArray();

    internal void VerifySnapshotLayout()
    {
        if (report is null || axis.ActualWidth <= 0 || ActualHeight < 100)
        {
            throw new InvalidOperationException("Battery timeline has no visible report or clock axis.");
        }
        for (int row = 0; row < lanes.Length; row++)
        {
            foreach (Border segment in lanes[row].Children.OfType<Border>())
            {
                double left = Canvas.GetLeft(segment);
                if (left < 0 || segment.Width <= 0 || left + segment.Width > axis.ActualWidth + 0.5
                    || segment.Child is not null || segment.ActualHeight <= 0)
                {
                    throw new InvalidOperationException("Battery interval escaped its lane or contains an inline label.");
                }
                if (row == 2 && (segment.BorderThickness.Left <= 0 || segment.Background == AccentStroke))
                {
                    throw new InvalidOperationException("Unknown intervals must have a neutral outline.");
                }
            }
        }
        double duration = (report.WindowEndUtc - report.WindowStartUtc).TotalSeconds;
        foreach (WindowsBatteryReportInterval interval in intervals)
        {
            int row = interval.PowerSource switch
            {
                WindowsBatteryPowerSource.Battery => 0,
                WindowsBatteryPowerSource.Ac => 1,
                _ => 2,
            };
            double expectedLeft = (interval.StartUtc - report.WindowStartUtc).TotalSeconds / duration * axis.ActualWidth;
            double expectedWidth = (interval.EndUtc - interval.StartUtc).TotalSeconds / duration * axis.ActualWidth;
            if (!lanes[row].Children.OfType<Border>().Any(segment =>
                Math.Abs(Canvas.GetLeft(segment) - expectedLeft) < 0.5 && Math.Abs(segment.Width - expectedWidth) < 0.5))
            {
                throw new InvalidOperationException("Battery interval source lane or time proportion is wrong.");
            }
        }
        if (startLabel.ActualWidth + endLabel.ActualWidth > axis.ActualWidth)
        {
            throw new InvalidOperationException("Battery timeline endpoint labels overlap.");
        }
        double right = startLabel.ActualWidth;
        foreach (TextBlock label in ticks.Children.OfType<TextBlock>())
        {
            double left = Canvas.GetLeft(label);
            if (left < right || left + label.ActualWidth > axis.ActualWidth - endLabel.ActualWidth)
            {
                throw new InvalidOperationException("Battery timeline clock labels overlap.");
            }
            right = left + label.ActualWidth;
        }
        if (!startLabel.Text.Contains('\n') || !endLabel.Text.Contains('\n'))
        {
            throw new InvalidOperationException("Battery timeline endpoint dates are missing.");
        }
    }
#endif
}
