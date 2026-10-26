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
        var cleanup = new IOException(
            @"cleanup failed at C:\Users\Alice\private.txt",
            new System.ComponentModel.Win32Exception(5));
        var wrapped = SnapTapDiagnosticFailure.FromException(
            "SnapTapSupervisor", "Cleanup", "Reconciliation", "CleanupFailed", cleanup, cause: original);
        Assert.True(wrapped.IsValid());
        Assert.True(wrapped.MayHaveSubmitted);
        Assert.Equal(5, wrapped.NativeErrorCode);
        Assert.Equal(original, wrapped.Cause);
        Assert.True(SnapTapDiagnosticFailure.Create(
            "SnapTapCoordinator", "StopChild", "Cleanup", "StopFailed", cause: original).MayHaveSubmitted);
        Assert.Contains("cleanup failed", wrapped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice", wrapped.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanupWrappingRetainsCurrentAggregateBranchesBesideOriginalCause()
    {
        var original = new DiagnosticFailure(
            "SnapTapWorker", "HookSession", "HookMutation", "WriteFailed",
            MayHaveSubmitted: true);
        var cleanup = new AggregateException(
            "cleanup failed",
            new IOException("close failed"),
            new System.ComponentModel.Win32Exception(5, "release failed"));

        DiagnosticFailure wrapped = SnapTapDiagnosticFailure.FromException(
            "SnapTapSupervisor",
            "Cleanup",
            "Reconciliation",
            "CleanupFailed",
            cleanup,
            cause: original);

        Assert.Equal(original, wrapped.Cause);
        Assert.Equal(2, wrapped.Related?.Count);
        Assert.Equal(nameof(IOException), wrapped.Related?[0].ExceptionType);
        Assert.Equal(nameof(System.ComponentModel.Win32Exception),
            wrapped.Related?[1].ExceptionType);
        Assert.True(wrapped.IsValid());
    }

    [Fact]
    public void CreateMarksTruncationWhenPriorFailureExceedsSharedDepth()
    {
        DiagnosticFailure cause = new("SnapTapWorker", "Run", "Failure", "Leaf");
        for (int index = 0; index < DiagnosticFailure.MaximumDepth + 2; index++)
        {
            cause = new DiagnosticFailure(
                "SnapTapWorker", "Run", "Failure", "Wrapped", Cause: cause);
        }

        DiagnosticFailure wrapped = SnapTapDiagnosticFailure.Create(
            "SnapTapCoordinator", "StopChild", "Cleanup", "StopFailed", cause: cause);

        Assert.True(wrapped.DetailsTruncated);
        Assert.True(wrapped.IsValid());
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
