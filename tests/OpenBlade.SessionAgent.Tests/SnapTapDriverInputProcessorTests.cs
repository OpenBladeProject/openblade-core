using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapDriverInputProcessorTests
{
    [Fact]
    public void AllFiveEventSequencesMatchIndependentPressOrderOracle()
    {
        SnapTapKeyStroke[] events = [new(0x1E, true), new(0x1E, false), new(0x20, true), new(0x20, false)];
        for (int sequence = 0; sequence < 1024; sequence++)
        {
            HashSet<ushort> delivered = [];
            var processor = new SnapTapDriverInputProcessor(batch =>
            {
                for (int index = 0; index < batch.Count; index++)
                {
                    SnapTapKeyStroke stroke = index == 0 ? batch.First : batch.Second;
                    if (stroke.IsDown) { delivered.Add(stroke.ScanCode); }
                    else { delivered.Remove(stroke.ScanCode); }
                    Assert.True(delivered.Count <= 1);
                }
                return batch.Count;
            });
            Assert.True(processor.Enable());
            Dictionary<ushort, int> pressedAt = [];
            int encoded = sequence;
            for (int step = 0; step < 5; step++)
            {
                SnapTapKeyStroke stroke = events[encoded % events.Length];
                encoded /= events.Length;
                if (stroke.IsDown) { pressedAt.TryAdd(stroke.ScanCode, step); }
                else { pressedAt.Remove(stroke.ScanCode); }
                Assert.True(processor.Handle(stroke));
                ushort winner = pressedAt.Count == 0 ? (ushort)0 : pressedAt.MaxBy(pair => pair.Value).Key;
                Assert.Equal(winner, processor.Winner);
                Assert.Equal(winner == 0 ? [] : new HashSet<ushort> { winner }, delivered);
            }
        }
    }

    [Fact]
    public void FailedOriginalForwardPoisonsWithoutRetryOrCleanupOutput()
    {
        int submissions = 0;
        var processor = new SnapTapDriverInputProcessor(_ => { submissions++; return 0; });
        Assert.True(processor.Enable());
        Assert.False(processor.Handle(new(0x1E, true)));
        Assert.True(processor.Faulted);
        Assert.False(processor.Handle(new(0x20, true)));
        processor.Disable();
        Assert.Equal(1, submissions);
    }

    [Fact]
    public void UnknownScanCodeNeverReachesOutput()
    {
        int submissions = 0;
        var processor = new SnapTapDriverInputProcessor(_ => { submissions++; return 1; });
        Assert.True(processor.Enable());
        Assert.False(processor.Handle(new(0x2C, true)));
        Assert.True(processor.Faulted);
        Assert.Equal(0, submissions);
    }

    [Fact]
    public void WaitingForReleaseForwardsHeldInputWithoutPriorityChanges()
    {
        List<SnapTapKeyStroke> delivered = [];
        var processor = new SnapTapDriverInputProcessor(batch =>
        {
            Assert.Equal(1, batch.Count);
            delivered.Add(batch.First);
            return 1;
        });
        Assert.True(processor.Handle(new(0x1E, true)));
        Assert.False(processor.Enable());
        Assert.True(processor.Handle(new(0x1E, false)));
        Assert.True(processor.ArePhysicallyReleased);
        Assert.True(processor.Enable());
        Assert.Equal(new[] { new SnapTapKeyStroke(0x1E, true), new SnapTapKeyStroke(0x1E, false) }, delivered);
    }

    [Fact]
    public void OutputExceptionPoisonsAndNeverForwardsTheNewWinner()
    {
        int submissions = 0;
        var processor = new SnapTapDriverInputProcessor(_ =>
        {
            if (++submissions == 2) { throw new IOException(); }
            return 1;
        });
        Assert.True(processor.Enable());
        Assert.True(processor.Handle(new(0x1E, true)));
        Assert.False(processor.Handle(new(0x20, true)));
        Assert.True(processor.Faulted);
        Assert.Equal(2, submissions);
    }
}
