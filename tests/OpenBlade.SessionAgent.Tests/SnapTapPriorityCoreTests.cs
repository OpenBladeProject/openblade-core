using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapPriorityCoreTests
{
    private const ushort A = 0x1E;
    private const ushort D = 0x20;

    [Fact]
    public void Handle_AllFiveEventSequencesMatchIndependentPressIndexOracle()
    {
        SnapTapKeyStroke[] events = [new(A, true), new(A, false), new(D, true), new(D, false)];
        for (int sequence = 0; sequence < 1024; sequence++)
        {
            List<SnapTapOutputBatch> actual = [];
            SnapTapPriorityCore core = new(batch => { actual.Add(batch); return batch.Count; });
            Assert.True(core.Enable());
            Dictionary<ushort, int> pressedAt = [];
            ushort previousWinner = 0;
            int encoded = sequence;
            for (int step = 0; step < 5; step++)
            {
                SnapTapKeyStroke key = events[encoded % 4];
                encoded /= 4;
                bool wasHeld = pressedAt.ContainsKey(key.ScanCode);
                if (key.IsDown)
                {
                    pressedAt.TryAdd(key.ScanCode, step);
                }
                else
                {
                    pressedAt.Remove(key.ScanCode);
                }

                ushort expectedWinner = pressedAt.Count == 0
                    ? (ushort)0
                    : pressedAt.MaxBy(pair => pair.Value).Key;
                List<SnapTapKeyStroke> expected = [];
                if (expectedWinner != previousWinner)
                {
                    if (previousWinner != 0)
                    {
                        expected.Add(new(previousWinner, false));
                    }
                    if (expectedWinner != 0)
                    {
                        expected.Add(new(expectedWinner, true));
                    }
                }
                else if (wasHeld && key.IsDown && key.ScanCode == expectedWinner)
                {
                    expected.Add(key);
                }

                actual.Clear();
                Assert.True(core.Handle(key));
                Assert.Equal(expectedWinner, core.Winner);
                Assert.Equal(pressedAt.Count == 0, core.ArePhysicallyReleased);
                Assert.Equal(expected.Count == 0 ? 0 : 1, actual.Count);
                if (expected.Count != 0)
                {
                    SnapTapOutputBatch batch = Assert.Single(actual);
                    Assert.Equal(expected.Count, batch.Count);
                    Assert.Equal(expected[0], batch.First);
                    Assert.Equal(expected.Count == 2 ? expected[1] : default, batch.Second);
                }
                previousWinner = expectedWinner;
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Handle_HandoffRepeatsAndBothReleaseOrders(bool releaseWinnerFirst)
    {
        List<SnapTapOutputBatch> sent = [];
        SnapTapPriorityCore core = new(batch => { sent.Add(batch); return batch.Count; });
        Assert.True(core.Enable());
        Assert.True(core.Handle(new(A, true)));
        Assert.True(core.Handle(new(D, true)));
        Assert.Equal(new(new(A, false), new(D, true), 2), sent[^1]);
        Assert.True(core.Handle(new(A, true)));
        Assert.Equal(2, sent.Count);
        Assert.Equal(D, core.Winner);
        Assert.True(core.Handle(new(D, true)));
        Assert.Equal(new(new(D, true), default, 1), sent[^1]);
        Assert.True(core.Handle(new(releaseWinnerFirst ? D : A, false)));
        Assert.Equal(releaseWinnerFirst ? A : D, core.Winner);
        if (releaseWinnerFirst)
        {
            Assert.Equal(new(new(D, false), new(A, true), 2), sent[^1]);
        }
        else
        {
            Assert.Equal(3, sent.Count);
        }
        Assert.True(core.Handle(new(releaseWinnerFirst ? A : D, false)));
        Assert.Equal(new(new(releaseWinnerFirst ? A : D, false), default, 1), sent[^1]);
        Assert.Equal((ushort)0, core.Winner);
        Assert.True(core.ArePhysicallyReleased);
    }

    [Fact]
    public void Disabled_TracksPhysicalEdgesAndWaitsForReleaseBeforeEnabling()
    {
        List<SnapTapOutputBatch> sent = [];
        SnapTapPriorityCore core = new(batch => { sent.Add(batch); return batch.Count; });
        Assert.False(core.Handle(new(A, true)));
        Assert.False(core.Handle(new(D, true)));
        Assert.False(core.Enable());
        Assert.False(core.Handle(new(A, false)));
        Assert.False(core.Enable());
        Assert.False(core.Handle(new(D, false)));
        Assert.True(core.ArePhysicallyReleased);
        Assert.True(core.Enable());
        Assert.Empty(sent);
        Assert.True(core.Handle(new(A, true)));
        Assert.False(core.Enable());
        core.Disable();
        Assert.False(core.Enabled);
        Assert.Equal(new(new(A, false), default, 1), sent[^1]);
        core.Disable();
        Assert.Equal(2, sent.Count);
        Assert.False(core.Enable());
        Assert.False(core.Handle(new(A, false)));
        Assert.True(core.Enable());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Handoff_FailedSubmissionPermanentlyPoisonsWithoutRetry(int accepted, bool throws)
    {
        int calls = 0;
        SnapTapPriorityCore core = new(batch =>
        {
            calls++;
            if (calls == 1)
            {
                return batch.Count;
            }
            if (throws)
            {
                throw new InvalidOperationException("Sender failed after possible submission.");
            }
            return accepted;
        });
        Assert.True(core.Enable());
        Assert.True(core.Handle(new(A, true)));
        Assert.False(core.Handle(new(D, true)));
        AssertPermanentlyPoisoned(core);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Handle_ReentrantPoisonIsNotOverwrittenBySuccessfulSender()
    {
        SnapTapPriorityCore? core = null;
        int calls = 0;
        core = new(batch => { calls++; core!.Poison(); return batch.Count; });
        Assert.True(core.Enable());
        Assert.False(core.Handle(new(A, true)));
        AssertPermanentlyPoisoned(core);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disable_FailurePoisonsAndNeverRetriesCleanup(bool throws)
    {
        int calls = 0;
        SnapTapPriorityCore core = new(batch =>
        {
            calls++;
            if (calls == 1)
            {
                return batch.Count;
            }
            if (throws)
            {
                throw new InvalidOperationException();
            }
            return 0;
        });
        Assert.True(core.Enable());
        Assert.True(core.Handle(new(A, true)));
        core.Disable();
        AssertPermanentlyPoisoned(core);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Handle_UnselectedScansAreIgnored()
    {
        SnapTapPriorityCore core = new(_ => throw new InvalidOperationException());
        Assert.False(core.Handle(new(0x11, true)));
        Assert.True(core.ArePhysicallyReleased);
        Assert.True(core.Enable());
        Assert.False(core.Handle(new(0x11, true)));
        Assert.False(core.Handle(new(0x11, false)));
        Assert.True(core.Enabled);
        Assert.False(core.Faulted);
        Assert.Equal((ushort)0, core.Winner);
    }

    private static void AssertPermanentlyPoisoned(SnapTapPriorityCore core)
    {
        Assert.True(core.Faulted);
        Assert.False(core.Enabled);
        Assert.Equal((ushort)0, core.Winner);
        core.Disable();
        Assert.False(core.Handle(new(A, false)));
        Assert.False(core.Handle(new(D, false)));
        Assert.True(core.ArePhysicallyReleased);
        Assert.False(core.Enable());
        Assert.False(core.Handle(new(A, true)));
        core.Disable();
        Assert.True(core.Faulted);
        Assert.Equal((ushort)0, core.Winner);
    }
}
