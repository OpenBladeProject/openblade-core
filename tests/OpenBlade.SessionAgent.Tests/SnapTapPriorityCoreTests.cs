using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapPriorityCoreTests
{
    private const ushort A = 0x1E;
    private const ushort D = 0x20;
    private static readonly SnapTapInputResult Passed = new(true, false);
    private static readonly SnapTapInputResult Suppressed = new(true, true);

    [Fact]
    public void Handle_AllFiveEventSequencesMatchIndependentPressIndexOracle()
    {
        SnapTapKeyStroke[] events = [new(A, true), new(A, false), new(D, true), new(D, false)];
        for (int sequence = 0; sequence < 1024; sequence++)
        {
            List<SnapTapOutputBatch> injected = [];
            SnapTapPriorityCore core = new(batch => { injected.Add(batch); return batch.Count; });
            Assert.True(core.Enable());
            Dictionary<ushort, int> pressedAt = [];
            HashSet<ushort> logicalDown = [];
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
                    ? (ushort)0 : pressedAt.MaxBy(pair => pair.Value).Key;
                injected.Clear();
                SnapTapInputResult result = core.Handle(key);
                Assert.True(result.Handled);
                if (key.IsDown && !wasHeld)
                {
                    Assert.False(result.Suppress);
                }
                // This verifies the planned order only. Windows delivery ordering
                // between SendInput and a forwarded hook event needs a native test.
                foreach (SnapTapOutputBatch batch in injected)
                {
                    Assert.InRange(batch.Count, 1, 2);
                    Apply(batch.First);
                    if (batch.Count == 2)
                    {
                        Apply(batch.Second);
                    }
                }
                if (!result.Suppress)
                {
                    Apply(key);
                }
                Assert.Equal(expectedWinner, core.Winner);
                Assert.Equal(pressedAt.Count == 0, core.ArePhysicallyReleased);
                Assert.Equal(expectedWinner == 0 ? Array.Empty<ushort>() : [expectedWinner], logicalDown.ToArray());

                void Apply(SnapTapKeyStroke output)
                {
                    if (output.IsDown)
                    {
                        logicalDown.Add(output.ScanCode);
                    }
                    else
                    {
                        logicalDown.Remove(output.ScanCode);
                    }
                    Assert.True(logicalDown.Count <= 1, "The planned output must never hold A and D together.");
                }
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Handle_PreservesPhysicalPressesAndWinnerRepeatsAcrossBothReleaseOrders(bool releaseWinnerFirst)
    {
        List<SnapTapOutputBatch> sent = [];
        SnapTapPriorityCore core = new(batch => { sent.Add(batch); return batch.Count; });
        Assert.True(core.Enable());
        Assert.Equal(Passed, core.Handle(new(A, true)));
        Assert.Equal(Passed, core.Handle(new(A, true)));
        Assert.Empty(sent);
        Assert.Equal(Passed, core.Handle(new(D, true)));
        Assert.Equal(new(new(A, false), default, 1), Assert.Single(sent));
        Assert.Equal(Suppressed, core.Handle(new(A, true)));
        Assert.Equal(Passed, core.Handle(new(D, true)));
        Assert.Single(sent);
        Assert.Equal(D, core.Winner);
        Assert.Equal(releaseWinnerFirst ? Suppressed : Passed,
            core.Handle(new(releaseWinnerFirst ? D : A, false)));
        Assert.Equal(releaseWinnerFirst ? A : D, core.Winner);
        if (releaseWinnerFirst)
        {
            Assert.Equal(2, sent.Count);
            Assert.Equal(new(new(D, false), new(A, true), 2), sent[^1]);
            Assert.Equal(Passed, core.Handle(new(A, true)));
            Assert.Equal(2, sent.Count);
        }
        else
        {
            Assert.Single(sent);
        }
        int beforeFinalRelease = sent.Count;
        Assert.Equal(Passed, core.Handle(new(releaseWinnerFirst ? A : D, false)));
        Assert.Equal(beforeFinalRelease, sent.Count);
        Assert.Equal((ushort)0, core.Winner);
        Assert.True(core.ArePhysicallyReleased);
        Assert.Equal(Passed, core.Handle(new(D, true)));
        Assert.Equal(Passed, core.Handle(new(A, true)));
        Assert.Equal(new(new(D, false), default, 1), sent[^1]);
    }

    [Fact]
    public void Disabled_TracksPhysicalEdgesAndWaitsForReleaseBeforeEnabling()
    {
        List<SnapTapOutputBatch> sent = [];
        SnapTapPriorityCore core = new(batch => { sent.Add(batch); return batch.Count; });
        Assert.Equal(default, core.Handle(new(A, true)));
        Assert.Equal(default, core.Handle(new(D, true)));
        Assert.False(core.Enable());
        Assert.Equal(default, core.Handle(new(A, false)));
        Assert.False(core.Enable());
        Assert.Equal(default, core.Handle(new(D, false)));
        Assert.True(core.ArePhysicallyReleased);
        Assert.True(core.Enable());
        Assert.Empty(sent);
        Assert.Equal(Passed, core.Handle(new(A, true)));
        Assert.False(core.Enable());
        core.Disable();
        Assert.False(core.Enabled);
        Assert.Equal(new(new(A, false), default, 1), Assert.Single(sent));
        core.Disable();
        Assert.Single(sent);
        Assert.False(core.Enable());
        Assert.Equal(default, core.Handle(new(A, false)));
        Assert.True(core.Enable());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void Handoff_FailurePermanentlyPoisonsAndOnlySuppressesFreshDown(bool restoring, int failure)
    {
        int calls = 0;
        SnapTapPriorityCore? core = null;
        core = new(batch =>
        {
            calls++;
            if (restoring && calls == 1)
            {
                return batch.Count;
            }
            if (failure == 2)
            {
                throw new InvalidOperationException("Submission is uncertain.");
            }
            if (failure == 3)
            {
                core!.Poison();
                return batch.Count;
            }
            return failure;
        });
        Assert.True(core.Enable());
        Assert.Equal(Passed, core.Handle(new(A, true)));
        if (restoring)
        {
            Assert.Equal(Passed, core.Handle(new(D, true)));
        }
        Assert.Equal(new(false, !restoring), core.Handle(new(D, !restoring)));
        AssertPermanentlyPoisoned(core);
        Assert.Equal(restoring ? 2 : 1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Disable_FailurePoisonsAndNeverRetriesCleanup(int failure)
    {
        int calls = 0;
        SnapTapPriorityCore? core = null;
        core = new(batch =>
        {
            calls++;
            if (failure == 1)
            {
                throw new InvalidOperationException();
            }
            if (failure == 2)
            {
                core!.Poison();
                return batch.Count;
            }
            return 0;
        });
        Assert.True(core.Enable());
        Assert.Equal(Passed, core.Handle(new(A, true)));
        core.Disable();
        AssertPermanentlyPoisoned(core);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Handle_UnselectedScansAreIgnored()
    {
        SnapTapPriorityCore core = new(_ => throw new InvalidOperationException());
        Assert.Equal(default, core.Handle(new(0x11, true)));
        Assert.True(core.ArePhysicallyReleased);
        Assert.True(core.Enable());
        Assert.Equal(default, core.Handle(new(0x11, true)));
        Assert.Equal(default, core.Handle(new(0x11, false)));
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
        Assert.Equal(default, core.Handle(new(A, false)));
        Assert.Equal(default, core.Handle(new(D, false)));
        Assert.True(core.ArePhysicallyReleased);
        Assert.False(core.Enable());
        Assert.Equal(default, core.Handle(new(A, true)));
        core.Disable();
        Assert.True(core.Faulted);
        Assert.Equal((ushort)0, core.Winner);
    }
}
