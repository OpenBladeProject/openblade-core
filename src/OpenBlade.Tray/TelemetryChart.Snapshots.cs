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
            rangeEndInput.Text = FormatRangeTime(range.SourceEndUtc.AddSeconds(1));
            CommitRangeTime(rangeEndInput);
            overview.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.25 });
        }
        else if (destination is "telemetry-expired" or "telemetry-retention"
            && retainedSnapshot is { } retained && timeRange is { } current && rawSamples.Count > 1)
        {
            localizationSnapshotInspectionFraction = null;
            selectedSampleIndex = destination == "telemetry-expired" ? 0 : rawSamples.Count - 1;
            PinSelection();
            DateTimeOffset newStart = rawSamples[1].TimestampUtc;
            TelemetryHistorySnapshot trimmed = TelemetryChartViewport.BuildSnapshot(retained, newStart, current.SourceEndUtc);
            Update(trimmed, newStart, current.SourceEndUtc.AddSeconds(1), sourceSessionId);
            analysisPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
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
        if (PresentationTimestamp.ForDisplay(timeRange.StartUtc).Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Disconnected telemetry snapshots did not pin displayed timestamps to UTC.");
        }
        TelemetryTimeRangeSelection savedRange = timeRange;
        HistorySelectionNotice savedNotice = historySelectionNotice;
        bool savedPinned = samplePinned;
        bool savedFollow = followLatest;
        bool savedFullRange = showAllRetained;
        int savedIndex = selectedSampleIndex;
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
            historySelectionNotice = savedNotice;
            samplePinned = savedPinned;
            followLatest = savedFollow;
            showAllRetained = savedFullRange;
            selectedSampleIndex = savedIndex;
            RebuildViewport();
        }
        else if (historySelectionNotice != HistorySelectionNotice.SampleExpired)
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
        VerifyLocalizationSnapshotRangeInteractions();
        var extreme = TelemetryTimeRangeSelection.Create(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        _ = ApplyRangeDrag(extreme, RangeDragKind.Start, -extreme.Duration.Ticks);
        _ = ApplyRangeDrag(extreme, RangeDragKind.End, extreme.Duration.Ticks);
        if (historySelectionNotice != savedNotice)
        {
            throw new InvalidOperationException("Telemetry verification changed the visible retention notice.");
        }
        if (SelectedSample is null && lanes.SelectMany(lane => lane.Markers.Values).Any(marker => marker.Visibility == Visibility.Visible))
        {
            throw new InvalidOperationException("An empty telemetry chart contains a phantom selected sample marker.");
        }
    }

    private void VerifyLocalizationSnapshotRangeInteractions()
    {
        if (retainedSnapshot is not { } savedRetained || timeRange is not { } savedRange || rawSamples.Count < 2)
        {
            return;
        }
        IReadOnlyList<TelemetrySample> savedRaw = rawSamples;
        int savedIndex = selectedSampleIndex;
        bool savedFollow = followLatest;
        bool savedPinned = samplePinned;
        bool savedFullRange = showAllRetained;
        HistorySelectionNotice savedNotice = historySelectionNotice;
        Visibility savedValidationVisibility = rangeValidation.Visibility;
        string savedValidationText = rangeValidation.Text;
        double? savedFraction = localizationSnapshotInspectionFraction;
        localizationSnapshotInspectionFraction = null;
        try
        {
            selectedSampleIndex = 0;
            PinSelection();
            SelectTimeRange(ApplyRangeDrag(savedRange, RangeDragKind.Pan, 0));
            RebuildViewport();
            if (samplePinned || followLatest || timeRange.FollowLatest)
            {
                throw new InvalidOperationException("Changing the telemetry range pinned a sample or resumed following.");
            }
            InspectAtFraction(1);
            if (SelectedSample?.TimestampUtc != rawSamples[^1].TimestampUtc)
            {
                throw new InvalidOperationException("Hover inspection did not resume after a telemetry range edit.");
            }

            PinSelection();
            rangeStartInput.Text = FormatRangeTime(timeRange.EndUtc.AddSeconds(1));
            CommitRangeTime(rangeStartInput);
            if (samplePinned || rangeValidation.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("A clamped time edit pinned a sample or omitted its adjustment message.");
            }

            timeRange = savedRange;
            rawSamples = savedRaw;
            selectedSampleIndex = 0;
            historySelectionNotice = HistorySelectionNotice.None;
            PinSelection();
            DateTimeOffset retainedStart = savedRaw[1].TimestampUtc;
            TelemetryHistorySnapshot trimmed = TelemetryChartViewport.BuildSnapshot(
                savedRetained, retainedStart, savedRange.SourceEndUtc);
            Update(trimmed, retainedStart, savedRange.SourceEndUtc.AddSeconds(1), sourceSessionId);
            if (SelectedSample is not null || samplePinned
                || historySelectionNotice != HistorySelectionNotice.SampleExpired
                || !followLatestButton.IsEnabled || !nextSampleButton.IsEnabled)
            {
                throw new InvalidOperationException("An expired pinned sample was replaced or has no keyboard recovery.");
            }
            Update(trimmed, retainedStart, savedRange.SourceEndUtc.AddSeconds(2), sourceSessionId);
            if (SelectedSample is not null || historySelectionNotice != HistorySelectionNotice.SampleExpired)
            {
                throw new InvalidOperationException("An expired selection silently changed on the next live update.");
            }
            InspectAtFraction(1);
            if (SelectedSample is null || historySelectionNotice != HistorySelectionNotice.None)
            {
                throw new InvalidOperationException("Hover could not recover after the pinned reading expired.");
            }

            retainedSnapshot = savedRetained;
            timeRange = savedRange;
            rawSamples = savedRaw;
            selectedSampleIndex = savedRaw.Count - 1;
            PinSelection();
            DateTimeOffset surviving = SelectedSample!.TimestampUtc;
            Update(trimmed, retainedStart, savedRange.SourceEndUtc.AddSeconds(1), sourceSessionId);
            if (SelectedSample?.TimestampUtc != surviving || !samplePinned
                || historySelectionNotice != HistorySelectionNotice.RangeAdjusted)
            {
                throw new InvalidOperationException("Retention lost a surviving pin or hid the range adjustment.");
            }
        }
        finally
        {
            retainedSnapshot = savedRetained;
            timeRange = savedRange;
            rawSamples = savedRaw;
            selectedSampleIndex = savedIndex;
            followLatest = savedFollow;
            samplePinned = savedPinned;
            showAllRetained = savedFullRange;
            historySelectionNotice = savedNotice;
            rangeValidation.Visibility = savedValidationVisibility;
            rangeValidation.Text = savedValidationText;
            localizationSnapshotInspectionFraction = savedFraction;
            RebuildViewport();
        }
    }
}
#endif
