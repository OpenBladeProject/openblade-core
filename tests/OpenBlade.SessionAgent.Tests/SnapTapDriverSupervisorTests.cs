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
        string previous = DateTime.UtcNow.AddDays(-4).ToString("O");
        string current = DateTime.UtcNow.AddDays(-1).ToString("O");
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(previous, previous, []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(null, current, []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(previous, "", []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(previous, current,
            [RazerSnapTapDriverProtocol.EncodeHook(0x1E)]));
        Assert.True(SnapTapDriverSupervisor.CanDiscardJournal(previous, current, []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(current, previous, []));
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
    [Fact]
    public void ForegroundPauseRequiresDedicatedExitAndFreshUnambiguousStatus()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var status = new SnapTapRuntimeStatus(SnapTapRuntimeState.Paused, start.AddMilliseconds(1), 123,
            Failure: SnapTapDiagnosticFailure.Create("SnapTapWorker", "Run", "InputLoop", "ForegroundUnavailable"));
        bool Eligible(int code, SnapTapRuntimeStatus? value) =>
            SnapTapDriverSupervisor.IsCleanForegroundPause(code, value, 123, start);

        Assert.True(Eligible(SnapTapDriverLoop.ForegroundPauseExitCode, status));
        foreach (int code in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 9 })
        {
            Assert.False(Eligible(code, status));
        }
        Assert.False(Eligible(8, null));
        Assert.False(Eligible(8, status with { ProcessId = 124 }));
        Assert.False(Eligible(8, status with { UpdatedAt = start.AddMilliseconds(-1) }));
        Assert.False(Eligible(8, status with { State = SnapTapRuntimeState.Failed }));
        Assert.False(Eligible(8, status with { Failure = null }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { MayHaveSubmitted = true } }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Reason = "ForegroundChanged" } }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Operation = "HookSession" } }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Cause = status.Failure } }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Related = [status.Failure!] } }));
    }

    [Fact]
    public void ReconciliationConfirmsEachOwnedRemovalAndPreservesOriginalBaseline()
    {
        byte[] baseline = new byte[292];
        baseline[4] = 2;
        var transport = new ReconciliationTransport(baseline,
            RazerSnapTapDriverProtocol.EncodeHook(0x1E), RazerSnapTapDriverProtocol.EncodeHook(0x20));
        List<bool> pending = [];
        Assert.True(SnapTapDriverSupervisor.TryReconcileHooks(transport, [baseline], pending.Add, out var failure));
        Assert.Null(failure);
        Assert.Equal(baseline, Assert.Single(transport.Hooks));
        Assert.Equal(new[] { "read", "clear:30", "read", "clear:32", "read" }, transport.Calls);
        Assert.Equal(new[] { true, false, true, false }, pending);
        transport.Calls.Clear();
        Assert.True(SnapTapDriverSupervisor.TryReconcileHooks(transport, [baseline], pending.Add, out _));
        Assert.Equal(new[] { "read" }, transport.Calls);
    }

    [Fact]
    public void ChangedOwnershipPreventsAnyCleanupMutation()
    {
        byte[] foreign = RazerSnapTapDriverProtocol.EncodeHook(0x1E);
        foreign[16] = 1;
        var transport = new ReconciliationTransport(foreign, RazerSnapTapDriverProtocol.EncodeHook(0x20));
        Assert.False(SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => Assert.Fail(), out var failure));
        Assert.Equal("HookOwnershipChanged", failure);
        Assert.Equal(new[] { "read" }, transport.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UncertainClearOrReadbackStopsBeforeSecondRemoval(bool failDuringClear)
    {
        var transport = new ReconciliationTransport(
            RazerSnapTapDriverProtocol.EncodeHook(0x1E), RazerSnapTapDriverProtocol.EncodeHook(0x20))
        { FailDuringClear = failDuringClear, FailReadback = !failDuringClear };
        bool pending = false;
        Assert.Throws<IOException>(() => SnapTapDriverSupervisor.TryReconcileHooks(
            transport, [], value => pending = value, out _));
        Assert.True(pending);
        Assert.DoesNotContain("clear:32", transport.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnconfirmedOrForeignReadbackCannotRearm(bool ignoreClear)
    {
        var transport = new ReconciliationTransport(
            RazerSnapTapDriverProtocol.EncodeHook(0x1E), RazerSnapTapDriverProtocol.EncodeHook(0x20))
        { IgnoreClear = ignoreClear, ChangeReadback = !ignoreClear };
        Assert.False(SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => { }, out var failure));
        Assert.Equal("HookCleanupUnconfirmed", failure);
        Assert.DoesNotContain("clear:32", transport.Calls);
    }

    private sealed class ReconciliationTransport(params byte[][] hooks) : IRazerSnapTapHookTransport
    {
        internal List<byte[]> Hooks { get; } = hooks.ToList();
        internal List<string> Calls { get; } = [];
        internal bool FailDuringClear { get; init; }
        internal bool FailReadback { get; init; }
        internal bool IgnoreClear { get; init; }
        internal bool ChangeReadback { get; init; }
        private bool cleared;

        public IReadOnlyList<byte[]> ReadHooks()
        {
            Calls.Add("read");
            if (cleared && FailReadback) { throw new IOException("readback failed"); }
            if (cleared && ChangeReadback) { Hooks[0][16] = 4; }
            return Hooks.Select(value => (byte[])value.Clone()).ToArray();
        }

        public void ClearHook(byte[] hook)
        {
            ushort scan = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(hook.AsSpan(0x0A));
            Calls.Add($"clear:{scan}");
            if (FailDuringClear) { throw new IOException("clear failed after possible submission"); }
            cleared = true;
            if (!IgnoreClear)
            {
                Hooks.RemoveAll(value => value.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeHook(scan)));
            }
        }

        public void SetHook(byte[] hook) => Assert.Fail("Reconciliation must not install hooks.");
        public void SetHookEnabled(bool enabled) => Assert.Fail("Reconciliation must not reset aggregate input.");
        public void CloseSubscription() => Assert.Fail("The guardian must never subscribe.");
    }

}
