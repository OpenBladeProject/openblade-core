using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class LowLevelKeyboardHookHealthTrackerTests
{
    [Fact]
    public void ConsumeMatchesOneExactHookObservation()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        tracker.Record(scanCode: 0x3B, keyDown: true, messageTime: 100);

        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            messageTime: 110));
        Assert.True(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            messageTime: 100));
        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            messageTime: 100));
    }

    [Fact]
    public void ConsumeRejectsWrongDirectionAndMessageTime()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        tracker.Record(scanCode: 0x3B, keyDown: true, messageTime: 100);
        tracker.Record(scanCode: 0x3C, keyDown: false, messageTime: 110);

        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: false,
            messageTime: 100));
        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            messageTime: 101));
        Assert.True(tracker.Consume(
            scanCode: 0x3C,
            keyDown: false,
            messageTime: 110));
    }

    [Fact]
    public void RecordBoundsUnmatchedHistory()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        for (uint index = 0;
             index < LowLevelKeyboardHookHealthTracker.MaximumRecentEvents + 4;
             index++)
        {
            tracker.Record(index, keyDown: true, messageTime: index);
        }

        Assert.False(tracker.Consume(
            scanCode: 0,
            keyDown: true,
            messageTime: 0));
        Assert.True(tracker.Consume(
            scanCode: 4,
            keyDown: true,
            messageTime: 4));
    }

    [Fact]
    public void ClearDropsAllObservations()
    {
        var tracker = new LowLevelKeyboardHookHealthTracker();
        tracker.Record(scanCode: 0x3B, keyDown: true, messageTime: 100);

        tracker.Clear();

        Assert.False(tracker.Consume(
            scanCode: 0x3B,
            keyDown: true,
            messageTime: 100));
    }
}
