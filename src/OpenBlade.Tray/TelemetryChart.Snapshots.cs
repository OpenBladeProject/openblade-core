#if OPENBLADE_DEVELOPER_HARNESS
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class TelemetryChart
{
    internal void ShowLocalizationSnapshotLayout(string destination)
    {
        if (destination == "telemetry-range" && timeRange is { } range)
        {
            showAllRetained = false;
            timeRange = range.MoveStart(range.SourceStartUtc.AddSeconds(2), TimeSpan.FromSeconds(1))
                .MoveEnd(range.SourceStartUtc.AddSeconds(22), TimeSpan.FromSeconds(1));
            RebuildViewport();
            overview.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.25 });
        }
        else if (destination is "telemetry-analysis" or "telemetry-narrow" or "telemetry-dark")
        {
            rangeStatisticsExpander.IsExpanded = destination == "telemetry-narrow";
            analysisPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
        }
    }

    internal Flyout GetLocalizationSnapshotFlyout(bool color)
    {
        if (!color)
        {
            return metricEditorFlyout ?? throw new InvalidOperationException("The telemetry metric editor is absent.");
        }
        Flyout flyout = colorEditors[choices[0].Metric];
        if (flyout.Content is StackPanel content && content.Children.OfType<ColorPicker>().FirstOrDefault() is { } picker)
        {
            picker.Color = ParseColor(ActualTheme == ElementTheme.Dark ? choices[0].DarkColor : choices[0].LightColor);
        }
        return flyout;
    }

    internal void VerifyLocalizationSnapshotAnalysis()
    {
        if (retainedSnapshot is null || timeRange is null)
        {
            throw new InvalidOperationException("Telemetry snapshot data was not initialized.");
        }
        TelemetryTimeRangeSelection savedRange = timeRange;
        DateTimeOffset? selected = SelectedSample?.TimestampUtc;
        foreach (MetricChoice choice in choices)
        {
            AnalysisRow row = analysisRows[choice.Metric];
            if (row.Cells[1].Text != FormatNumber(SelectedSample?.Value(choice.Metric), choice))
            {
                throw new InvalidOperationException("A telemetry table value does not refer to the selected raw sample.");
            }
            int count = rawSamples.Count(sample => sample.Value(choice.Metric).HasValue);
            if (row.Cells[5].Text != count.ToString("N0"))
            {
                throw new InvalidOperationException("Telemetry statistics did not use all raw readings in the selected range.");
            }
            if (!ReferenceEquals(row.Cells[1].Style, AppTypography.RequireStyle(AppTypography.Data)))
            {
                throw new InvalidOperationException("Telemetry values did not use the shared numeric typography role.");
            }
        }
        if (selected is not null)
        {
            PinSelection();
            Update(retainedSnapshot, savedRange.SourceStartUtc, savedRange.SourceEndUtc.AddSeconds(1), sourceSessionId);
            if (SelectedSample?.TimestampUtc != selected || timeRange.StartUtc != savedRange.StartUtc
                || timeRange.EndUtc != savedRange.EndUtc || timeRange.FollowLatest)
            {
                throw new InvalidOperationException("A pinned telemetry moment or range moved with an incoming live update.");
            }
            timeRange = savedRange;
            RebuildViewport();
        }
        else
        {
            PinSelection();
            if (samplePinned || !followLatest || !timeRange.FollowLatest)
            {
                throw new InvalidOperationException("An empty chart froze live range growth without an actual sample selection.");
            }
        }
        string exact = FormatRangeTime(savedRange.StartUtc.AddTicks(123));
        if (!DateTimeOffset.TryParseExact(exact, RangeTimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset parsed) || parsed != savedRange.StartUtc.AddTicks(123))
        {
            throw new InvalidOperationException("Telemetry time bounds lost precision or their UTC offset.");
        }
        var extreme = TelemetryTimeRangeSelection.Create(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        _ = ApplyRangeDrag(extreme, RangeDragKind.Start, -extreme.Duration.Ticks);
        _ = ApplyRangeDrag(extreme, RangeDragKind.End, extreme.Duration.Ticks);
        if (SelectedSample is null && lanes.SelectMany(lane => lane.Markers.Values).Any(marker => marker.Visibility == Visibility.Visible))
        {
            throw new InvalidOperationException("An empty telemetry chart contains a phantom selected sample marker.");
        }
    }
}
#endif
