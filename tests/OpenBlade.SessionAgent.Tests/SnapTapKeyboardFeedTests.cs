using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapKeyboardFeedTests
{
    [Fact]
    public void DrainDeliversPostedPacketsWithTheirDirection()
    {
        var feed = new SnapTapKeyboardFeed();
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(new(0x1E, true), TimeSpan.Zero);
        tracker.Expect(new(0x1E, false), TimeSpan.Zero);

        feed.Post(0x1E, 0x0000);
        feed.Post(0x1E, 0x0001);
        feed.Drain(tracker);

        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void EachDrainCountsAsAPump()
    {
        var feed = new SnapTapKeyboardFeed();
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(new(0x20, true), TimeSpan.Zero);
        for (int index = 0; index < SnapTapDeliveryTracker.MinimumPumps; index++)
        {
            feed.Drain(tracker);
        }

        Assert.NotNull(tracker.Check(SnapTapDeliveryTracker.Deadline));
    }

    [Fact]
    public void OverflowClearsPendingKeysInsteadOfReportingALoss()
    {
        var feed = new SnapTapKeyboardFeed();
        var tracker = new SnapTapDeliveryTracker();
        for (int index = 0; index <= SnapTapKeyboardFeed.Capacity; index++)
        {
            feed.Post(0x10, 0);
        }
        tracker.Expect(new(0x1E, true), TimeSpan.Zero);
        for (int index = 0; index < SnapTapDeliveryTracker.MinimumPumps; index++)
        {
            feed.Drain(tracker);
        }

        Assert.Equal(0, tracker.PendingCount);
        Assert.Null(tracker.Check(TimeSpan.FromSeconds(10)));
    }
}
