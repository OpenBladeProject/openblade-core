using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapRestoredRepeatTests
{
    [Theory]
    [InlineData(0x1E, 0x20)]
    [InlineData(0x20, 0x1E)]
    public void RestoredHold_RepeatsAtCadenceWithoutCatchupAndStopsOnRelease(int older, int newer)
    {
        var time = new ManualTime();
        List<SnapTapOutputBatch> sent = [];
        var core = new SnapTapPriorityCore(batch => { sent.Add(batch); return batch.Count; },
            time, TimeSpan.FromMilliseconds(40));
        Assert.True(core.Enable());
        core.Handle(new((ushort)older, true));
        time.Advance(1000);
        core.RepeatRestoredWinner();
        Assert.Empty(sent); // Ordinary holds retain hardware repeats.
        core.Handle(new((ushort)newer, true));
        core.Handle(new((ushort)newer, false));
        sent.Clear();
        time.Advance(39);
        core.RepeatRestoredWinner();
        Assert.Empty(sent);
        time.Advance(1);
        core.RepeatRestoredWinner();
        Assert.Equal(new(new((ushort)older, true), default, 1), Assert.Single(sent));
        time.Advance(4000);
        core.RepeatRestoredWinner();
        core.RepeatRestoredWinner();
        Assert.Equal(2, sent.Count); // No burst after a stalled message loop.
        time.Advance(40);
        core.RepeatRestoredWinner();
        Assert.Equal(3, sent.Count);
        core.Handle(new((ushort)older, false));
        time.Advance(1000);
        core.RepeatRestoredWinner();
        Assert.Equal(3, sent.Count);
        Assert.Equal((ushort)0, core.Winner);
    }

    [Theory]
    [InlineData(0)] // disable
    [InlineData(1)] // poison / foreground loss
    [InlineData(2)] // physical winner repeats resume
    [InlineData(3)] // a new physical priority press
    public void RestoredHold_StopsSyntheticRepeatsWhenOwnershipChanges(int change)
    {
        var time = new ManualTime();
        List<SnapTapOutputBatch> sent = [];
        var core = new SnapTapPriorityCore(batch => { sent.Add(batch); return batch.Count; },
            time, TimeSpan.FromMilliseconds(40));
        core.Enable();
        core.Handle(new(0x1E, true));
        core.Handle(new(0x20, true));
        core.Handle(new(0x20, false));
        switch (change)
        {
            case 0: core.Disable(); break;
            case 1: core.Poison(); break;
            case 2: Assert.False(core.Handle(new(0x1E, true)).Suppress); break;
            case 3: core.Handle(new(0x20, true)); break;
        }
        sent.Clear();
        time.Advance(1000);
        core.RepeatRestoredWinner();
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatFailure_PoisonsWithoutAnotherAttemptOrCleanup(bool throws)
    {
        var time = new ManualTime();
        int calls = 0;
        var core = new SnapTapPriorityCore(batch =>
        {
            calls++;
            if (calls < 3)
            {
                return batch.Count;
            }
            if (throws)
            {
                throw new InvalidOperationException("Uncertain output");
            }
            return 0;
        }, time, TimeSpan.FromMilliseconds(40));
        core.Enable();
        core.Handle(new(0x1E, true));
        core.Handle(new(0x20, true));
        core.Handle(new(0x20, false));
        time.Advance(40);
        core.RepeatRestoredWinner();
        Assert.True(core.Faulted);
        core.Disable();
        time.Advance(1000);
        core.RepeatRestoredWinner();
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HardwareTakeover_SuppressesOneDownOnlyAfterSyntheticRepeat(bool timerFired)
    {
        var time = new ManualTime();
        int calls = 0;
        var core = new SnapTapPriorityCore(batch => { calls++; return batch.Count; },
            time, TimeSpan.FromMilliseconds(40));
        core.Enable();
        core.Handle(new(0x1E, true));
        core.Handle(new(0x20, true));
        core.Handle(new(0x20, false));
        if (timerFired)
        {
            time.Advance(40);
            core.RepeatRestoredWinner();
        }
        int beforeTakeover = calls;
        Assert.Equal(timerFired, core.Handle(new(0x1E, true)).Suppress);
        Assert.False(core.Handle(new(0x1E, true)).Suppress);
        time.Advance(1000);
        core.RepeatRestoredWinner();
        Assert.Equal(beforeTakeover, calls);
    }

    [Fact]
    public void NonIntegralCadence_PreservesPhaseAcrossTimerTicks()
    {
        var time = new ManualTime();
        int calls = 0;
        var core = new SnapTapPriorityCore(batch => { calls++; return batch.Count; },
            time, TimeSpan.FromMilliseconds(33.333));
        core.Enable();
        core.Handle(new(0x1E, true));
        core.Handle(new(0x20, true));
        core.Handle(new(0x20, false));
        calls = 0;
        for (int i = 0; i < 100; i++)
        {
            time.Advance(10);
            core.RepeatRestoredWinner();
        }
        Assert.Equal(30, calls);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(int milliseconds) => timestamp += milliseconds;
    }
}
