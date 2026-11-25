using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapDeliveryTrackerTests
{
    private static readonly SnapTapKeyStroke ADown = new(0x1E, true);
    private static readonly SnapTapKeyStroke AUp = new(0x1E, false);
    private static readonly SnapTapKeyStroke DDown = new(0x20, true);

    [Fact]
    public void DeliveredKeysNeverReportALoss()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(ADown, TimeSpan.Zero);
        tracker.Expect(AUp, TimeSpan.FromMilliseconds(5));
        tracker.Observe(0x1E, isUp: false);
        tracker.Observe(0x1E, isUp: true);
        Pump(tracker, 10);

        Assert.Equal(0, tracker.PendingCount);
        Assert.Null(tracker.Check(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void UndeliveredKeyIsLostOnlyAfterBothTheDeadlineAndEnoughPumps()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(ADown, TimeSpan.Zero);
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);

        Assert.Null(tracker.Check(SnapTapDeliveryTracker.Deadline - TimeSpan.FromMilliseconds(1)));

        var starved = new SnapTapDeliveryTracker();
        starved.Expect(ADown, TimeSpan.Zero);
        Pump(starved, SnapTapDeliveryTracker.MinimumPumps - 1);
        // A worker that has not drained its own Raw Input queue cannot blame the driver.
        Assert.Null(starved.Check(TimeSpan.FromSeconds(10)));

        var failure = tracker.Check(SnapTapDeliveryTracker.Deadline);
        Assert.NotNull(failure);
        Assert.Equal("OutputNotDelivered", failure.Reason);
        Assert.True(failure.MayHaveSubmitted);
        Assert.Contains("Sent=1; Delivered=0; Pending=1", failure.Context);
        Assert.DoesNotContain("0x1E", failure.Context);
    }

    [Fact]
    public void ObservationsMatchOnlyTheSameScanCodeAndDirection()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(ADown, TimeSpan.Zero);
        tracker.Observe(0x1E, isUp: true);
        tracker.Observe(0x20, isUp: false);
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);

        Assert.Equal(1, tracker.PendingCount);
        Assert.NotNull(tracker.Check(SnapTapDeliveryTracker.Deadline));
    }

    [Fact]
    public void UnexpectedPhysicalKeysAreIgnored()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Observe(0x20, isUp: false);
        tracker.Expect(DDown, TimeSpan.Zero);
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);

        // An earlier D from another source does not satisfy a later expectation.
        Assert.NotNull(tracker.Check(SnapTapDeliveryTracker.Deadline));
    }

    [Fact]
    public void RepeatedDownsAreMatchedOneForOne()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(ADown, TimeSpan.Zero);
        tracker.Expect(ADown, TimeSpan.FromMilliseconds(30));
        tracker.Observe(0x1E, isUp: false);
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);

        Assert.Equal(1, tracker.PendingCount);
        Assert.NotNull(tracker.Check(TimeSpan.FromMilliseconds(30) + SnapTapDeliveryTracker.Deadline));
    }

    [Fact]
    public void TheOldestPendingKeySetsTheDeadline()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(ADown, TimeSpan.Zero);
        tracker.Expect(DDown, TimeSpan.FromMilliseconds(400));
        tracker.Observe(0x1E, isUp: false);
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);

        Assert.Null(tracker.Check(SnapTapDeliveryTracker.Deadline));
        Assert.NotNull(tracker.Check(TimeSpan.FromMilliseconds(400) + SnapTapDeliveryTracker.Deadline));
    }

    [Fact]
    public void AFreezeGivesPendingKeysAFreshDeadline()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(ADown, TimeSpan.Zero);
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);
        TimeSpan resumed = TimeSpan.FromSeconds(3);
        tracker.Rebase(resumed);
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);

        Assert.Null(tracker.Check(resumed + TimeSpan.FromSeconds(1)));
        Assert.NotNull(tracker.Check(resumed + SnapTapDeliveryTracker.Deadline));

        var starved = new SnapTapDeliveryTracker();
        starved.Expect(ADown, TimeSpan.Zero);
        Pump(starved, SnapTapDeliveryTracker.MinimumPumps);
        starved.Rebase(resumed);
        Pump(starved, SnapTapDeliveryTracker.MinimumPumps - 1);
        Assert.Null(starved.Check(resumed + SnapTapDeliveryTracker.Deadline));
    }

    [Fact]
    public void ClearingUnobservableKeysPreventsALoss()
    {
        var tracker = new SnapTapDeliveryTracker();
        tracker.Expect(ADown, TimeSpan.Zero);
        tracker.Clear();
        Pump(tracker, SnapTapDeliveryTracker.MinimumPumps);

        Assert.Null(tracker.Check(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TooManyPendingKeysReportALossImmediately()
    {
        var tracker = new SnapTapDeliveryTracker();
        for (int index = 0; index <= SnapTapDeliveryTracker.Capacity; index++)
        {
            tracker.Expect(ADown, TimeSpan.Zero);
        }

        var failure = tracker.Check(TimeSpan.Zero);
        Assert.NotNull(failure);
        Assert.Contains("Overflow=True", failure.Context);
    }

    private static void Pump(SnapTapDeliveryTracker tracker, int count)
    {
        for (int index = 0; index < count; index++)
        {
            tracker.Pumped();
        }
    }
}
