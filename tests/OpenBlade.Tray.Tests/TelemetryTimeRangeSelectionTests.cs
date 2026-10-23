using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryTimeRangeSelectionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_UsesFullOrRequestedRangeAtLatestSourceEdge()
    {
        TelemetryTimeRangeSelection full = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddMinutes(10));
        TelemetryTimeRangeSelection bounded = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddMinutes(10),
            TimeSpan.FromMinutes(3));

        Assert.Equal(Start, full.StartUtc);
        Assert.Equal(Start.AddMinutes(10), full.EndUtc);
        Assert.True(full.FollowLatest);
        Assert.Equal(Start.AddMinutes(7), bounded.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(3), bounded.Duration);
    }

    [Fact]
    public void HandlesSingleSampleAndEqualBounds()
    {
        TelemetryTimeRangeSelection selection = TelemetryTimeRangeSelection.Create(Start, Start);

        Assert.Equal(TimeSpan.Zero, selection.Duration);
        Assert.Equal(
            selection.SetFollowLatest(false),
            selection.MoveStart(Start.AddMinutes(1), TimeSpan.FromSeconds(1)));
        Assert.Equal(
            selection.SetFollowLatest(false),
            selection.MoveEnd(Start.AddMinutes(-1), TimeSpan.FromSeconds(1)));
        Assert.Equal(
            selection.SetFollowLatest(false),
            selection.Pan(TimeSpan.FromMinutes(1)));
        Assert.Equal(selection, selection.UpdateSource(Start, Start));
    }

    [Fact]
    public void HandlesCrossingEdgesAndMinimumSpan()
    {
        TelemetryTimeRangeSelection selection = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddMinutes(10),
            TimeSpan.FromMinutes(6));

        TelemetryTimeRangeSelection startCrossed = selection.MoveStart(
            Start.AddMinutes(20),
            TimeSpan.FromMinutes(2));
        Assert.Equal(Start.AddMinutes(8), startCrossed.StartUtc);
        Assert.Equal(Start.AddMinutes(10), startCrossed.EndUtc);
        Assert.False(startCrossed.FollowLatest);

        TelemetryTimeRangeSelection endCrossed = selection.MoveEnd(
            Start,
            TimeSpan.FromMinutes(2));
        Assert.Equal(Start.AddMinutes(4), endCrossed.StartUtc);
        Assert.Equal(Start.AddMinutes(6), endCrossed.EndUtc);

        TelemetryTimeRangeSelection shortSource = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddSeconds(1));
        Assert.Equal(
            shortSource.SetFollowLatest(false),
            shortSource.MoveStart(Start.AddSeconds(1), TimeSpan.FromMinutes(1)));

        TelemetryTimeRangeSelection leftWindow = selection.Pan(TimeSpan.FromHours(-1));
        TelemetryTimeRangeSelection maximumAvailable = leftWindow.MoveStart(
            Start.AddMinutes(10),
            TimeSpan.FromMinutes(20));
        Assert.Equal(Start, maximumAvailable.StartUtc);
        Assert.Equal(Start.AddMinutes(6), maximumAvailable.EndUtc);
    }

    [Fact]
    public void Pan_PreservesDurationAndClampsToRetention()
    {
        TelemetryTimeRangeSelection selection = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddMinutes(10),
            TimeSpan.FromMinutes(3));

        TelemetryTimeRangeSelection left = selection.Pan(TimeSpan.FromHours(-1));
        Assert.Equal(Start, left.StartUtc);
        Assert.Equal(Start.AddMinutes(3), left.EndUtc);
        Assert.False(left.FollowLatest);

        TelemetryTimeRangeSelection right = left.Pan(TimeSpan.FromHours(1));
        Assert.Equal(Start.AddMinutes(7), right.StartUtc);
        Assert.Equal(Start.AddMinutes(10), right.EndUtc);
    }

    [Fact]
    public void SourceAdvance_PreservesPinOrMovesFollowWindow()
    {
        TelemetryTimeRangeSelection following = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddMinutes(10),
            TimeSpan.FromMinutes(3));
        TelemetryTimeRangeSelection advanced = following.UpdateSource(
            Start,
            Start.AddMinutes(12));
        Assert.Equal(Start.AddMinutes(9), advanced.StartUtc);
        Assert.Equal(Start.AddMinutes(12), advanced.EndUtc);

        TelemetryTimeRangeSelection pinned = advanced.SetFollowLatest(false);
        TelemetryTimeRangeSelection stillPinned = pinned.UpdateSource(
            Start,
            Start.AddMinutes(15));
        Assert.Equal(pinned.StartUtc, stillPinned.StartUtc);
        Assert.Equal(pinned.EndUtc, stillPinned.EndUtc);
        Assert.False(stillPinned.FollowLatest);

        TelemetryTimeRangeSelection resumed = stillPinned.SetFollowLatest(true);
        Assert.Equal(Start.AddMinutes(12), resumed.StartUtc);
        Assert.Equal(Start.AddMinutes(15), resumed.EndUtc);
        Assert.True(resumed.FollowLatest);
    }

    [Fact]
    public void RetentionAdvanceAndSourceResetClampState()
    {
        TelemetryTimeRangeSelection pinned = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddMinutes(10),
            TimeSpan.FromMinutes(4))
            .SetFollowLatest(false);

        TelemetryTimeRangeSelection retained = pinned.UpdateSource(
            Start.AddMinutes(8),
            Start.AddMinutes(20));
        Assert.Equal(Start.AddMinutes(8), retained.StartUtc);
        Assert.Equal(Start.AddMinutes(12), retained.EndUtc);

        TelemetryTimeRangeSelection reset = retained.UpdateSource(
            Start.AddHours(1),
            Start.AddHours(1).AddSeconds(5),
            sourceReset: true);
        Assert.Equal(reset.SourceStartUtc, reset.StartUtc);
        Assert.Equal(reset.SourceEndUtc, reset.EndUtc);
        Assert.False(reset.FollowLatest);
    }

    [Fact]
    public void RejectsInvalidSourceWindowAndEditArguments()
    {
        Assert.Throws<ArgumentException>(() => TelemetryTimeRangeSelection.Create(
            Start.AddSeconds(1),
            Start));
        Assert.Throws<InvalidDataException>(() => TelemetryTimeRangeSelection.Create(
            Start.ToOffset(TimeSpan.FromHours(1)),
            Start.AddHours(1)));
        TelemetryTimeRangeSelection point = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddSeconds(1),
            TimeSpan.Zero);
        Assert.Equal(point.StartUtc, point.EndUtc);

        TelemetryTimeRangeSelection selection = TelemetryTimeRangeSelection.Create(
            Start,
            Start.AddSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            selection.MoveStart(Start, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            selection.MoveEnd(Start, TimeSpan.Zero));
    }
}
