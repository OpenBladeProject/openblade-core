namespace OpenBlade.Tray.Tests;

public sealed class PresentationTimestampTests
{
    [Fact]
    public void IntervalEndKeepsTimeOnlyOnSameDisplayedDay()
    {
        var start = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset end = start.AddHours(2);
        Assert.Equal(end.ToString("t"), PresentationTimestamp.FormatIntervalEnd(start, end));
    }

    [Fact]
    public void IntervalEndIncludesDateAcrossDisplayedMidnight()
    {
        var start = new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset end = start.AddHours(2);
        Assert.Equal(end.ToString("g"), PresentationTimestamp.FormatIntervalEnd(start, end));
    }

    [Fact]
    public void ProductionDisplayKeepsWindowsLocalTime()
    {
        var instant = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        Assert.Equal(instant.ToLocalTime(), PresentationTimestamp.ForDisplay(instant, snapshotMode: false));
        Assert.Equal(instant.ToLocalTime(), PresentationTimestamp.ForDisplay(instant));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(-7)]
    public void SnapshotDisplayIsUtcRegardlessOfInputOffset(int hours)
    {
        var instant = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        DateTimeOffset display = PresentationTimestamp.ForDisplay(instant.ToOffset(TimeSpan.FromHours(hours)), snapshotMode: true);
        Assert.Equal(TimeSpan.Zero, display.Offset);
        Assert.Equal(instant, display);
        Assert.Equal(instant.Ticks, display.Ticks);
    }

    [Fact]
    public void SnapshotDisplayRetainsExtremeUtcInstants()
    {
        Assert.Equal(DateTimeOffset.MinValue, PresentationTimestamp.ForDisplay(DateTimeOffset.MinValue, snapshotMode: true));
        Assert.Equal(DateTimeOffset.MaxValue, PresentationTimestamp.ForDisplay(DateTimeOffset.MaxValue, snapshotMode: true));
    }
}
