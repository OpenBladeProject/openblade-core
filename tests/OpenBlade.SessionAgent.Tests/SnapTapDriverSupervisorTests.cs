using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapDriverSupervisorTests
{
    [Fact]
    public void GuardianAllowsOnlyCanonicalAdditionsToItsBaseline()
    {
        byte[] unrelated = new byte[292];
        unrelated[4] = 2;
        byte[][] baseline = [unrelated];
        byte[][] current = [RazerSnapTapDriverProtocol.EncodeHook(0x1E), unrelated,
            RazerSnapTapDriverProtocol.EncodeHook(0x20)];
        Assert.True(SnapTapDriverSupervisor.SameUnownedHooks(baseline, current));
        Assert.False(SnapTapDriverSupervisor.SameUnownedHooks(baseline, current.Skip(1).Skip(1).ToArray()));
        byte[] changed = (byte[])unrelated.Clone();
        changed[20] = 1;
        Assert.False(SnapTapDriverSupervisor.SameUnownedHooks(baseline, [changed]));
        Assert.False(SnapTapDriverSupervisor.SameUnownedHooks(baseline, [unrelated, unrelated]));
    }

    [Fact]
    public void GuardianRejectsAnAdRecordThatDiffersFromItsOwnBytes()
    {
        byte[] foreign = RazerSnapTapDriverProtocol.EncodeHook(0x1E);
        foreign[16] = 1;
        Assert.False(SnapTapDriverSupervisor.SameUnownedHooks([], [foreign]));
    }

    [Fact]
    public void AmbiguousOutputJournalRequiresAnotherBootAndNoAdHooks()
    {
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal("boot1", "boot1", []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(null, "boot2", []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal("boot1", "", []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal("boot1", "boot2",
            [RazerSnapTapDriverProtocol.EncodeHook(0x1E)]));
        Assert.True(SnapTapDriverSupervisor.CanDiscardJournal("boot1", "boot2", []));
    }
}
