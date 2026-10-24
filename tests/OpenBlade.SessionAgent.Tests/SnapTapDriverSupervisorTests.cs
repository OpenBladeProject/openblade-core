using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapDriverSupervisorTests
{
    [Fact]
    public void CleanupWrappingRetainsBothNativeFailuresAndSubmissionUncertainty()
    {
        var original = new OpenBlade.Contracts.DiagnosticFailure(
            "SnapTapWorker", "HookSession", "HookMutation", "WriteFailed",
            MayHaveSubmitted: true, NativeErrorCode: 31);
        var cleanup = new IOException("private cleanup path", new System.ComponentModel.Win32Exception(5));
        var wrapped = SnapTapDiagnosticFailure.FromException(
            "SnapTapSupervisor", "Cleanup", "Reconciliation", "CleanupFailed", cleanup, cause: original);
        Assert.True(wrapped.IsValid());
        Assert.True(wrapped.MayHaveSubmitted);
        Assert.Equal(5, wrapped.NativeErrorCode);
        Assert.Equal(original, wrapped.Cause);
        Assert.True(SnapTapDiagnosticFailure.Create(
            "SnapTapCoordinator", "StopChild", "Cleanup", "StopFailed", cause: original).MayHaveSubmitted);
        Assert.DoesNotContain("private", wrapped.ToString(), StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public void WorkerFailureMustMatchFreshTerminalWorkerStatus()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var failure = new DiagnosticFailure("SnapTapWorker", "Run", "InputLoop", "OutputFailed", true);
        var matching = new SnapTapRuntimeStatus(
            SnapTapRuntimeState.Failed, startedAt.AddMilliseconds(1), 123, Failure: failure);

        Assert.Same(failure, SnapTapDriverSupervisor.MatchingTerminalFailure(matching, 123, startedAt));
        Assert.Null(SnapTapDriverSupervisor.MatchingTerminalFailure(matching, 124, startedAt));
        Assert.Null(SnapTapDriverSupervisor.MatchingTerminalFailure(
            matching with { UpdatedAt = startedAt.AddMilliseconds(-1) }, 123, startedAt));
        Assert.Null(SnapTapDriverSupervisor.MatchingTerminalFailure(
            matching with { State = SnapTapRuntimeState.Active, Failure = null }, 123, startedAt));
    }
}
