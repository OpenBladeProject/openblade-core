using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapWorkerProgressTests
{
    // Not a live process id, so no running supervisor shares the name.
    private static int UniqueParent() => -Random.Shared.Next(1, int.MaxValue);

    [Fact]
    public void WorkerStepsReachTheSupervisorWithElapsedAndSlowestStep()
    {
        long now = 1000;
        int parent = UniqueParent();
        using var supervisor = SnapTapWorkerProgress.Create(parent, () => now);
        using SnapTapWorkerProgress? worker = SnapTapWorkerProgress.TryOpen(parent, () => now);
        Assert.NotNull(worker);
        Assert.Equal(new SnapTapWorkerProgress.Snapshot("None", 0, 0, 0, "None", 0), supervisor.Read());

        worker.Enter("WaitingForRelease");
        now += 40;
        worker.Enter("HookMutation");
        now += 900;
        worker.Enter("HookReadback");
        worker.Admission(1700);
        worker.HooksSet(3);
        now += 250;

        SnapTapWorkerProgress.Snapshot snapshot = supervisor.Read();
        Assert.Equal(new SnapTapWorkerProgress.Snapshot("HookReadback", 250, 3, 1700, "HookMutation", 900), snapshot);
        Assert.Equal("WorkerStage=HookReadback; StageElapsedMs=250; HooksSet=3; LastAdmissionMs=1700; "
            + "SlowestStage=HookMutation; SlowestMs=900", snapshot.ToString());

        // Unknown names leave the record unchanged.
        worker.Enter("SubscriptionClose");
        worker.Enter("None");
        Assert.Equal("HookReadback", supervisor.Read().Stage);

        supervisor.Reset();
        Assert.Equal(new SnapTapWorkerProgress.Snapshot("None", 0, 0, 0, "None", 0), supervisor.Read());
    }

    [Fact]
    public void AMissingRecordOnlyLosesDiagnostics()
    {
        Assert.Null(SnapTapWorkerProgress.TryOpen(UniqueParent()));
    }

    [Fact]
    public void AdmissionTimeIsRecordedWhetherAdmissionPassesFailsOrThrows()
    {
        int parent = UniqueParent();
        using var supervisor = SnapTapWorkerProgress.Create(parent);
        using SnapTapWorkerProgress? worker = SnapTapWorkerProgress.TryOpen(parent);
        Assert.True(SnapTapDriverLoop.TimedAdmission(() => { Thread.Sleep(30); return true; }, worker));
        Assert.InRange(supervisor.Read().LastAdmissionMilliseconds, 1, 5000);
        Assert.False(SnapTapDriverLoop.TimedAdmission(() => false, worker));
        Assert.InRange(supervisor.Read().LastAdmissionMilliseconds, 0, 5000);
        worker!.Admission(-1);
        Assert.Throws<InvalidOperationException>(() =>
            SnapTapDriverLoop.TimedAdmission(() => throw new InvalidOperationException(), worker));
        Assert.InRange(supervisor.Read().LastAdmissionMilliseconds, 0, 5000);
        Assert.True(SnapTapDriverLoop.TimedAdmission(() => true, null));
    }

    [Fact]
    public void ADisposedRecordNeverThrowsIntoTheWorker()
    {
        var progress = SnapTapWorkerProgress.Create(UniqueParent());
        progress.Dispose();
        progress.Enter("HookMutation");
        progress.HooksSet(1);
        progress.Admission(1);
    }
}
