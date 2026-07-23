using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class LowLevelKeyboardHookHealthTrackerTests
{
    [Fact]
    public void ConsumeMatchesOneRecentHookObservation()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        tracker.Record(scanCode: 0x3B, keyDown: true, timestamp: 100);

        Assert.True(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            timestamp: 110,
            maximumAge: 20));
        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            timestamp: 111,
            maximumAge: 20));
    }

    [Fact]
    public void ConsumeRejectsWrongDirectionAndExpiredObservations()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        tracker.Record(scanCode: 0x3B, keyDown: true, timestamp: 100);
        tracker.Record(scanCode: 0x3C, keyDown: false, timestamp: 110);

        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: false,
            timestamp: 115,
            maximumAge: 20));
        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            timestamp: 130,
            maximumAge: 20));
        Assert.True(tracker.Consume(
            scanCode: 0x3C,
            keyDown: false,
            timestamp: 130,
            maximumAge: 20));
    }

    [Fact]
    public void RecordBoundsUnmatchedHistory()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        for (uint index = 0;
             index < LowLevelKeyboardHookHealthTracker.MaximumRecentEvents + 4;
             index++)
        {
            tracker.Record(index, keyDown: true, timestamp: index);
        }

        Assert.False(tracker.Consume(
            scanCode: 0,
            keyDown: true,
            timestamp: 100,
            maximumAge: 100));
        Assert.True(tracker.Consume(
            scanCode: 4,
            keyDown: true,
            timestamp: 100,
            maximumAge: 100));
    }

    [Fact]
    public void ClearDropsAllObservations()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        tracker.Record(scanCode: 0x3B, keyDown: true, timestamp: 100);

        tracker.Clear();

        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            timestamp: 101,
            maximumAge: 20));
    }
}
