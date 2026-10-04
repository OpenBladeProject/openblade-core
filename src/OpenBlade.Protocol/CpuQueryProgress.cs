using System.Diagnostics;

namespace OpenBlade.Protocol;

internal enum CpuQueryBucket { Other, FullAdmission, MutexWait, Continuity, NativeInvocation, MailboxPolling, Cleanup }

// Diagnostic-only state for one synchronous query. No callbacks, identifiers or
// native data; sampling never waits for the native worker. Durations are exclusive
// of nested buckets and include the currently pending operation at first timeout.
internal sealed class CpuQueryProgress
{
    private readonly object gate = new();
    private readonly double[] milliseconds = new double[7];
    private long changed = Stopwatch.GetTimestamp();
    private CpuQueryBucket bucket;
    private CpuQueryStage stage = CpuQueryStage.Approval;
    private CpuQueryOperation? operation;
    private int completed;
    private string? frozen;

    internal IDisposable? Enter(CpuQueryBucket next)
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null) { return null; }
                var previous = bucket; Accumulate(); bucket = next;
                return new Scope(this, previous);
            }
        }
        catch { return null; } // Diagnostic failure cannot deny or admit traffic.
    }
    internal void Boundary(CpuQueryStage next, CpuQueryOperation? nativeOperation = null)
    {
        try { lock (gate) { if (frozen is null) { stage = next; operation = nativeOperation; } } }
        catch { }
    }
    internal void CompletedIoctl()
    {
        try { lock (gate) { if (frozen is null) { completed = Math.Min(1000000, completed + 1); } } }
        catch { }
    }
    internal string Snapshot(bool freeze = false)
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null) { return frozen; }
                Accumulate();
                string detail = $"; native progress bucket {bucket}, stage {stage}, operation {operation?.ToString() ?? "None"}, completed IOCTLs {completed}; cumulative ms "
                    + string.Join(",", Enum.GetValues<CpuQueryBucket>().Select(value => $"{value}={(long)milliseconds[(int)value]}"));
                if (freeze) { frozen = detail; }
                return detail;
            }
        }
        catch { return "; native progress unavailable"; }
    }
    private void Accumulate()
    {
        long now = Stopwatch.GetTimestamp();
        milliseconds[(int)bucket] = Math.Min(600000, milliseconds[(int)bucket]
            + Stopwatch.GetElapsedTime(changed, now).TotalMilliseconds);
        changed = now;
    }
    private void Leave(CpuQueryBucket previous)
    {
        try { lock (gate) { if (frozen is null) { Accumulate(); bucket = previous; } } }
        catch { }
    }
    private sealed class Scope(CpuQueryProgress owner, CpuQueryBucket previous) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { owner.Leave(previous); } }
    }
}
