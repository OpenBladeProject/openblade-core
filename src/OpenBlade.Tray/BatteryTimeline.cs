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
    private readonly Canvas strip = new() { Height = 32 };
    private readonly TextBlock range = new() { TextWrapping = TextWrapping.Wrap };
    private WindowsBatteryReport? report;

    public BatteryTimeline()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(strip);
        panel.Children.Add(range);
        Children.Add(panel);
        strip.SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    public void Update(WindowsBatteryReport? value)
    {
        report = value;
        Draw();
    }

    private void Draw()
    {
        strip.Children.Clear();
        if (report is null || strip.ActualWidth <= 0)
        {
            range.Text = string.Empty;
            return;
        }
        range.Text = $"{report.WindowStartUtc.ToLocalTime():g} – {report.WindowEndUtc.ToLocalTime():g}";
        double duration = (report.WindowEndUtc - report.WindowStartUtc).TotalSeconds;
        if (duration <= 0)
        {
            return;
        }
        if (AccentStroke is not { } accent || UnknownStroke is not { } neutral
            || SegmentBackground is not { } background)
        {
            return;
        }
        foreach (WindowsBatteryReportInterval interval in report.Intervals)
        {
            double left = (interval.StartUtc - report.WindowStartUtc).TotalSeconds / duration * strip.ActualWidth;
            double width = (interval.EndUtc - interval.StartUtc).TotalSeconds / duration * strip.ActualWidth;
            string label = interval.PowerSource switch
            {
                WindowsBatteryPowerSource.Battery => LocalizedResources.Get("BatteryTimeline_Battery", "Battery"),
                WindowsBatteryPowerSource.Ac => LocalizedResources.Get("BatteryTimeline_AC", "AC"),
                _ => LocalizedResources.Get("BatteryTimeline_Unknown", "Unknown"),
            };
            var segment = new Border
            {
                Width = Math.Max(1, width - 1),
                Height = 32,
                Background = background,
                BorderBrush = interval.PowerSource == WindowsBatteryPowerSource.Unknown ? neutral : accent,
                BorderThickness = new Thickness(0, interval.PowerSource == WindowsBatteryPowerSource.Battery ? 4 : 1, 0, 0),
            };
            if (width >= 72)
            {
                segment.Child = new TextBlock
                {
                    Text = label,
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            string description = $"{interval.StartUtc.ToLocalTime():g} – {interval.EndUtc.ToLocalTime():t}: {label}";
            ToolTipService.SetToolTip(segment, description);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(segment, description);
            Canvas.SetLeft(segment, left);
            strip.Children.Add(segment);
        }
    }
}
