using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapDriverSupervisorTests
{
    [Theory]
    [InlineData(false, false, 10000, false)]
    [InlineData(false, false, 10001, true)]
    [InlineData(true, false, 1500, false)]
    [InlineData(true, false, 1501, true)]
    // A per-mutation admission measured 1.7 s under boot load; it must not end the worker.
    [InlineData(true, true, 1700, false)]
    [InlineData(true, true, 10000, false)]
    [InlineData(true, true, 10001, true)]
    public void HeartbeatBudgetExtendsOnlyForSignalledAdmission(bool started, bool admitting, long elapsed, bool expired)
    {
        Assert.Equal(expired, SnapTapDriverSupervisor.HeartbeatExpired(elapsed, started, admitting));
    }

    [Fact]
    public void AdmissionWindowSignalsProgressBeforeOpeningAndBeforeClosing()
    {
        using var window = new EventWaitHandle(false, EventResetMode.ManualReset);
        var events = new List<string>();
        void Progress() => events.Add(window.WaitOne(0) ? "progress:open" : "progress:closed");

        bool admitted = SnapTapDriverLoop.AdmitWithProgress(() =>
        {
            events.Add(window.WaitOne(0) ? "admit:open" : "admit:closed");
            return true;
        }, Progress, window);

        Assert.True(admitted);
        // The closing heartbeat precedes the reset, so the supervisor never sees a
        // closed window paired with the stale pre-admission heartbeat.
        Assert.Equal(["progress:closed", "admit:open", "progress:open"], events);
        Assert.False(window.WaitOne(0));
    }

    [Fact]
    public void AdmissionWindowClosesWhenAdmissionThrowsOrRejects()
    {
        using var window = new EventWaitHandle(false, EventResetMode.ManualReset);
        int progress = 0;
        Assert.Throws<InvalidOperationException>(() => SnapTapDriverLoop.AdmitWithProgress(
            () => throw new InvalidOperationException(), () => progress++, window));
        Assert.False(window.WaitOne(0));
        Assert.False(SnapTapDriverLoop.AdmitWithProgress(() => false, () => progress++, window));
        Assert.False(window.WaitOne(0));
        Assert.Equal(4, progress);
    }

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
    public void RecoveryRequiredKeepsTheWorkerExitAndItsOwnReasonBesideTheObservedCause()
    {
        var heartbeat = new DiagnosticFailure("SnapTapSupervisor", "Run", "InputLoop", "HeartbeatTimeout", false);
        var reported = new DiagnosticFailure("SnapTapWorker", "Run", "InputLoop", "OutputCountMismatch", false)
        {
            Context = new string('x', 20_000),
        };

        DiagnosticFailure failure = SnapTapDriverSupervisor.RecoveryRequiredFailure("InputLoop", heartbeat, 1, reported);

        Assert.Equal("RecoveryRequired", failure.Reason);
        Assert.Equal(1, failure.ProcessExitCode);
        Assert.Equal("HeartbeatTimeout", failure.Cause?.Reason);
        Assert.False(failure.MayHaveSubmitted);
        Assert.StartsWith("WorkerReason=OutputCountMismatch; WorkerStage=InputLoop; WorkerContext=x", failure.Context);
        Assert.True(failure.Context!.Length <= 8192);

        DiagnosticFailure unreported = SnapTapDriverSupervisor.RecoveryRequiredFailure("InputLoop", null, 4, null);
        Assert.Equal("WorkerReason=None; WorkerStage=None; WorkerContext=None", unreported.Context);
    }

    [Fact]
    public void ForegroundPauseRequiresDedicatedExitAndFreshUnambiguousStatus()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var status = new SnapTapRuntimeStatus(SnapTapRuntimeState.Paused, start.AddMilliseconds(1), 123,
            Failure: SnapTapDiagnosticFailure.Create("SnapTapWorker", "Run", "InputLoop", "ForegroundUnavailable"));
        bool Eligible(int code, SnapTapRuntimeStatus? value) =>
            SnapTapDriverSupervisor.IsCleanPause(code, value, 123, start);

        Assert.True(Eligible(SnapTapDriverLoop.CleanPauseExitCode, status));
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
    public void HeartbeatStallRearmsOnlyAfterFreshCleanWorkerStop()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        // The production timeout, so a renamed field cannot silently disable rearm.
        DiagnosticFailure stall = SnapTapDriverSupervisor.HeartbeatTimeoutFailure(started: true);
        Assert.Equal("InputLoop", stall.Stage);
        Assert.True(stall.MayHaveSubmitted);
        var off = new SnapTapRuntimeStatus(SnapTapRuntimeState.Off, start.AddMilliseconds(1), 123);
        var deferred = new SnapTapRuntimeStatus(SnapTapRuntimeState.Paused, start.AddMilliseconds(1), 123,
            Failure: SnapTapDriverLoop.SubscriptionDeferredFailure());
        bool Eligible(DiagnosticFailure? observed, int code, SnapTapRuntimeStatus? value) =>
            SnapTapDriverSupervisor.IsCleanStall(observed, code, value, 123, start);

        // The supervisor marks every timeout as possibly submitted; the worker's own
        // clean stop is what resolves that.
        Assert.True(Eligible(stall, 0, off));
        foreach (int code in new[] { 1, 2, 3, 4, 5, 6, 7, SnapTapDriverLoop.CleanPauseExitCode, -1 })
        {
            Assert.False(Eligible(stall, code, off));
        }
        Assert.False(Eligible(null, 0, off));
        Assert.False(Eligible(stall, 0, null));
        Assert.False(Eligible(stall, 0, off with { ProcessId = 124 }));
        Assert.False(Eligible(stall, 0, off with { UpdatedAt = start.AddMilliseconds(-1) }));
        Assert.False(Eligible(stall, 0, off with { State = SnapTapRuntimeState.Failed }));
        Assert.False(Eligible(stall, 0, off with { State = SnapTapRuntimeState.Active }));
        Assert.False(Eligible(stall, 0, off with { Failure = stall }));
        // A stall that lands in the next worker's hook setup ends with the dedicated
        // clean-pause exit; only that exit with its own fresh pause status qualifies.
        Assert.True(Eligible(stall, SnapTapDriverLoop.CleanPauseExitCode, deferred));
        Assert.False(Eligible(stall, 0, deferred));
        Assert.False(Eligible(stall, SnapTapDriverLoop.CleanPauseExitCode, deferred with { ProcessId = 124 }));
        Assert.False(Eligible(stall, SnapTapDriverLoop.CleanPauseExitCode,
            deferred with { Failure = deferred.Failure! with { MayHaveSubmitted = true } }));
        // A startup stall keeps its own budget and is not admitted here.
        Assert.False(Eligible(SnapTapDriverSupervisor.HeartbeatTimeoutFailure(started: false), 0, off));
        Assert.False(Eligible(stall with { Stage = "WorkerStartup" }, 0, off));
        Assert.False(Eligible(stall with { Reason = "WorkerDidNotExit" }, 0, off));
        Assert.False(Eligible(stall with { Component = SnapTapDiagnosticFailure.Worker }, 0, off));
        Assert.False(Eligible(stall with { Operation = "ReconcileHooks" }, 0, off));
        Assert.False(Eligible(stall with { Cause = stall }, 0, off));
        Assert.False(Eligible(stall with { Related = [stall] }, 0, off));
    }

    [Fact]
    public void StallRearmIsBoundedWithinItsWindow()
    {
        var recent = new Queue<long>();
        long window = SnapTapDriverSupervisor.StallRearmWindowMilliseconds;
        for (int index = 0; index < SnapTapDriverSupervisor.StallRearmLimit; index++)
        {
            Assert.True(SnapTapDriverSupervisor.TryAdmitStallRearm(recent, 1000 + index));
        }
        Assert.False(SnapTapDriverSupervisor.TryAdmitStallRearm(recent, 1000 + window - 1));
        // A refused attempt does not consume budget; the oldest rearm ages out exactly.
        Assert.True(SnapTapDriverSupervisor.TryAdmitStallRearm(recent, 1000 + window));
        Assert.False(SnapTapDriverSupervisor.TryAdmitStallRearm(recent, 1001 + window - 1));
        Assert.Equal(SnapTapDriverSupervisor.StallRearmLimit, recent.Count);
    }

    [Fact]
    public void DeferredSubscriptionIsACleanPauseOnlyInHookSetup()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var status = new SnapTapRuntimeStatus(SnapTapRuntimeState.Paused, start.AddMilliseconds(1), 123,
            Failure: SnapTapDriverLoop.SubscriptionDeferredFailure());
        bool Eligible(int code, SnapTapRuntimeStatus? value) =>
            SnapTapDriverSupervisor.IsCleanPause(code, value, 123, start);

        Assert.True(Eligible(SnapTapDriverLoop.CleanPauseExitCode, status));
        Assert.False(Eligible(1, status));
        Assert.False(Eligible(8, status with { State = SnapTapRuntimeState.Failed }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { MayHaveSubmitted = true } }));
        // Each reason is admitted only at the stage that produces it.
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Stage = "InputLoop" } }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Reason = "ForegroundUnavailable" } }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Reason = "EnableFailed" } }));
        Assert.False(Eligible(8, status with { Failure = status.Failure! with { Component = "SnapTapSupervisor" } }));
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
