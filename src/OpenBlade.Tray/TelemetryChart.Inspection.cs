using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Size = Windows.Foundation.Size;

namespace OpenBlade.Tray;

public sealed partial class TelemetryChart
{
    private static void CreateLaneReadout(Lane lane)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(lane.ReadoutTime);
        foreach (MetricChoice choice in lane.Metrics)
        {
            TextBlock value = Text(AppTypography.Data);
            lane.ReadoutValues.Add(choice.Metric, value);
            content.Children.Add(value);
        }
        lane.Readout.Child = content;
        lane.Readout.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["TelemetryReadoutStyle"];
    }

    private void PrepareLaneReadout(Lane lane, TelemetrySample? sample)
    {
        double availableWidth = lane.Plot.ActualWidth > plotLeft + PlotRight + 8
            ? lane.Plot.ActualWidth - plotLeft - PlotRight - 8 : 320;
        lane.Readout.MaxWidth = Math.Min(320, availableWidth);
        lane.ReadoutTime.Text = sample is null ? string.Empty : PresentationTimestamp.ForDisplay(sample.TimestampUtc).ToString("T");
        foreach (MetricChoice choice in lane.Metrics)
        {
            TextBlock value = lane.ReadoutValues[choice.Metric];
            value.Visibility = IsMetricVisible(choice) ? Visibility.Visible : Visibility.Collapsed;
            value.Foreground = SeriesBrush(choice);
            value.Text = $"{choice.Label}: {FormatNumber(sample?.Value(choice.Metric), choice)} {choice.Unit}";
        }
        // Measure the actual localized/scaled content; a callout must fit inside its lane.
        // A non-interactive overlay leaves pointer inspection on the plot.
        lane.Readout.Visibility = sample is null ? Visibility.Collapsed : Visibility.Visible;
        lane.Readout.Measure(new Size(lane.Readout.MaxWidth, double.PositiveInfinity));
    }

    private void UpdateLaneReadout(Lane lane, TelemetrySample sample, double x)
    {
        PrepareLaneReadout(lane, sample);
        lane.Readout.Visibility = Visibility.Visible;
        lane.Plot.Height = Math.Max(lane.Plot.Height, lane.Readout.DesiredSize.Height + PlotTop + plotBottom + 8);
        double width = lane.Readout.DesiredSize.Width;
        double rightEdge = plotLeft + lane.AreaWidth;
        // Put the values beside the crosshair and flip at the right edge.
        double left = x + 10;
        if (left + width > rightEdge)
        {
            left = x - width - 10;
        }
        Canvas.SetLeft(lane.Readout, Math.Clamp(left, plotLeft, Math.Max(plotLeft, rightEdge - width)));
        Canvas.SetTop(lane.Readout, PlotTop + 4);
    }
}
