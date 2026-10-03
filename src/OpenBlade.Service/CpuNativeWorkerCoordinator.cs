using System.Runtime.CompilerServices;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal enum CpuNativeWorkerKind { None, Observation, Admission, Submission }
internal enum CpuNativeWorkerFamily { Curve, Power }
internal sealed record CpuNativeWorkerStatus(bool WorkerOutstanding, CpuNativeWorkerKind Kind, bool Invalid,
    CpuLiveCurveNativeResult? CurveResult, CpuLivePowerNativeResult? PowerResult);

// One actual-worker owner per cached native session, covering provider/WMI work
// before Protocol acquires its separate native-operation latch. No authority is
// granted by joining this state, and replacing a backend cannot reset it.
internal sealed class CpuNativeWorkerCoordinator
{
    private static readonly ConditionalWeakTable<WindowsRz090528CpuNativeSession, CpuNativeWorkerCoordinator> Workers = new();
    private readonly object gate = new();
    private Task? active;
    private CpuNativeWorkerKind kind;
    private bool invalid;
    private CpuLiveCurveNativeResult? curveResult;
    private CpuLivePowerNativeResult? powerResult;
    private CpuNativeWorkerCoordinator() { }
    internal static CpuNativeWorkerCoordinator ForSession(WindowsRz090528CpuNativeSession session) => Workers.GetValue(session, static _ => new());
    internal Task? OutstandingWorker { get { lock (gate) { return active; } } }
    internal CpuNativeWorkerStatus Status { get { lock (gate) { return new(active is not null, kind, invalid, curveResult, powerResult); } } }
    internal void Invalidate() { lock (gate) { invalid = true; } }
    internal void Record(CpuLiveCurveNativeResult result) { lock (gate) { curveResult = result; } }
    internal void Record(CpuLivePowerNativeResult result) { lock (gate) { powerResult = result; } }

    internal Task<T> Start<T>(CpuNativeWorkerFamily family, CpuNativeWorkerKind nextKind,
        Func<CancellationToken, T> work, T refused, CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic = null)
    {
        if (token.IsCancellationRequested) { return Task.FromCanceled<T>(token); }
        Task<T> worker; CancellationTokenSource linked;
        lock (gate)
        {
            if (invalid || active is not null)
            { diagnostic?.Check(false, invalid ? "native worker scope invalid" : "native worker outstanding"); return Task.FromResult(refused); }
            linked = CancellationTokenSource.CreateLinkedTokenSource(token); kind = nextKind;
            if (nextKind == CpuNativeWorkerKind.Submission)
            { if (family == CpuNativeWorkerFamily.Curve) { curveResult = null; } else { powerResult = null; } }
            // Even queued cancellation executes its terminal cleanup path.
            worker = Task.Run(() =>
            {
                try { linked.Token.ThrowIfCancellationRequested(); return work(linked.Token); }
                catch (Exception exception)
                {
                    // Cancellation belongs to the initiating transaction's timeout
                    // classification; a late worker cannot replace its first failure.
                    if (exception is not OperationCanceledException)
                    { diagnostic?.Check(false, "native observation exception " + CpuQueryDiagnostic.FromException(CpuQueryStage.Approval, exception).Failure); }
                    Invalidate(); return refused;
                }
            });
            active = worker;
        }
        var terminal = worker.ContinueWith(completed =>
        {
            _ = completed.Exception; linked.Dispose();
            lock (gate)
            {
                if (completed.IsFaulted || completed.IsCanceled) { invalid = true; }
                if (ReferenceEquals(active, completed)) { active = null; kind = CpuNativeWorkerKind.None; }
            }
            return completed.Status == TaskStatus.RanToCompletion ? completed.Result : refused;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // Cancel only the caller wait; retain the original actual task and CTS.
        return terminal.WaitAsync(token);
    }
}
