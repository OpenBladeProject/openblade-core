using System.Diagnostics;

namespace OpenBlade.Protocol;

internal enum CpuQueryBucket { Other, FullAdmission, MutexWait, Continuity, NativeInvocation, MailboxPolling, Cleanup }
internal enum CpuQueryMailboxPhase { None, ReadinessWait, Preparation, Submission, ResponseWait, ArgumentRead }

// Diagnostic-only state for one synchronous query. No callbacks, identifiers or
// raw payloads; only a rejected mailbox status word is retained. Sampling never
// waits for the native worker. Durations are exclusive of nested buckets and
// include the current operation at the first timeout or mailbox rejection.
internal sealed class CpuQueryProgress
{
    private readonly object gate = new();
    private readonly double[] milliseconds = new double[7];
    private long changed = Stopwatch.GetTimestamp();
    private CpuQueryBucket bucket;
    private CpuQueryStage stage = CpuQueryStage.Approval;
    private CpuQueryOperation? operation;
    private int completed;
    private CpuQueryMailboxPhase mailboxPhase;
    private int readinessZeros, responseZeros;
    private bool clearAttempted, clearCompleted, submissionAttempted, submissionCompleted;
    private string? frozen;
    private uint? rejectedMailboxResponse;

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
    // Attempted is conservative: the two-call select/write sequence may enter.
    // Completed means both transport calls returned and their replies passed the
    // existing wire checks. Neither flag attests CPU settings or firmware state.
    internal void MailboxPhase(CpuQueryMailboxPhase next)
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null) { return; }
                mailboxPhase = next;
                if (next == CpuQueryMailboxPhase.ReadinessWait)
                {
                    readinessZeros = responseZeros = 0;
                    rejectedMailboxResponse = null;
                    clearAttempted = clearCompleted = submissionAttempted = submissionCompleted = false;
                }
                if (next == CpuQueryMailboxPhase.Preparation) { clearAttempted = true; }
                if (next == CpuQueryMailboxPhase.Submission) { submissionAttempted = true; }
            }
        }
        catch { }
    }
    internal void MailboxTransportCompleted(bool submission)
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null) { return; }
                if (submission) { submissionCompleted = true; }
                else { clearCompleted = true; }
            }
        }
        catch { }
    }
    internal void MailboxZero(bool afterSubmission)
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null) { return; }
                if (afterSubmission) { responseZeros = Math.Min(1000, responseZeros + 1); }
                else { readinessZeros = Math.Min(1000, readinessZeros + 1); }
            }
        }
        catch { }
    }
    internal void RejectMailboxResponse(uint response)
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null) { return; }
                rejectedMailboxResponse = response;
                Snapshot(freeze: true);
            }
        }
        catch { } // Diagnostic failure cannot change the existing rejection.
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
                    + string.Join(",", Enum.GetValues<CpuQueryBucket>().Select(value => $"{value}={(long)milliseconds[(int)value]}"))
                    + $"; current mailbox {mailboxPhase}, readiness zeros {readinessZeros}, response zeros {responseZeros}, clear attempted/completed {clearAttempted}/{clearCompleted}, submission attempted/completed {submissionAttempted}/{submissionCompleted}"
                    + (rejectedMailboxResponse is { } response ? $"; rejected mailbox response 0x{response:X8}" : string.Empty);
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

internal enum CpuObservationCost { ProcessScan, ServiceScan, GetCore0x5E, PMCopy }

// One request, four fixed counters. The ambient reference flows into existing
// workers; freezing it makes late completions inert, even during a new request.
internal sealed class CpuObservationCosts : IDisposable
{
    private static readonly AsyncLocal<CpuObservationCosts?> Current = new();
    private static readonly AsyncLocal<Frame?> CurrentFrame = new();
    private readonly object gate = new();
    private readonly CpuObservationCosts? previous;
    private readonly Func<long> timestamp;
    private readonly long started;
    private readonly long[] ticks = new long[4];
    private readonly int[] attempted = new int[4], completed = new int[4];
    private readonly List<Frame> active = [];
    private string? frozen;

    private CpuObservationCosts(Func<long> timestamp)
    {
        this.timestamp = timestamp;
        started = timestamp();
        previous = Current.Value;
    }

    internal static CpuObservationCosts? Begin(bool enabled, Func<long>? timestamp = null)
    {
        if (!enabled) { return null; }
        try
        {
            var result = new CpuObservationCosts(timestamp ?? Stopwatch.GetTimestamp);
            Current.Value = result;
            return result;
        }
        catch { return null; }
    }

    internal static CpuObservationCosts? BeginValidation(bool live, System.Reflection.Assembly assembly)
    {
        try
        {
            return Begin(live && assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Any(version => version.InformationalVersion.Contains("+voltage-validation.", StringComparison.Ordinal)));
        }
        catch { return null; }
    }

    internal static T Measure<T>(CpuObservationCost cost, Func<T> action)
    {
        Frame? scope = Current.Value?.Enter(cost);
        bool returned = false;
        try { T result = action(); returned = true; return result; }
        finally { scope?.Owner.Leave(scope, returned); }
    }

    private Frame? Enter(CpuObservationCost cost)
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null || active.Count >= 64) { return null; }
                Frame? parent = CurrentFrame.Value;
                if (parent?.Owner != this) { parent = null; }
                // A source may delegate to the instrumented inventory itself.
                if (parent?.Cost == cost) { return null; }
                var next = new Frame(this, cost, parent, timestamp());
                active.Add(next);
                attempted[(int)cost] = Math.Min(1000000, attempted[(int)cost] + 1);
                CurrentFrame.Value = next;
                return next;
            }
        }
        catch { return null; }
    }

    private void Leave(Frame scope, bool returned)
    {
        try
        {
            lock (gate)
            {
                if (frozen is null)
                {
                    long elapsed = Math.Max(0, timestamp() - scope.Started);
                    ticks[(int)scope.Cost] += Math.Max(0, elapsed - scope.ChildTicks);
                    if (returned) { completed[(int)scope.Cost] = Math.Min(1000000, completed[(int)scope.Cost] + 1); }
                    if (scope.Parent is { } parent) { parent.ChildTicks += elapsed; }
                    active.Remove(scope);
                }
            }
        }
        catch { }
        finally { try { CurrentFrame.Value = scope.Parent; } catch { } }
    }

    internal string Freeze()
    {
        try
        {
            lock (gate)
            {
                if (frozen is not null) { return frozen; }
                long now = timestamp();
                long[] totals = (long[])ticks.Clone();
                foreach (Frame scope in active)
                {
                    long children = scope.ChildTicks;
                    foreach (Frame child in active)
                    { if (child.Parent == scope) { children += Math.Max(0, now - child.Started); } }
                    totals[(int)scope.Cost] += Math.Max(0, now - scope.Started - children);
                }
                long wall = Math.Max(0, now - started);
                static string Ms(long value) => (value * 1000d / Stopwatch.Frequency)
                    .ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                frozen = "; CPU observation cost " + string.Join(", ", Enum.GetValues<CpuObservationCost>()
                    .Select(cost => $"{cost} attempted/completed={attempted[(int)cost]}/{completed[(int)cost]} exclusive-ms={Ms(totals[(int)cost])}"))
                    + $"; wall-ms={Ms(wall)}, unclassified-ms={Ms(Math.Max(0, wall - totals.Sum()))}";
                active.Clear();
                return frozen;
            }
        }
        catch { return "; CPU observation cost unavailable"; }
    }

    public void Dispose()
    {
        Freeze();
        try { Current.Value = previous; } catch { }
    }

    private sealed class Frame(CpuObservationCosts owner, CpuObservationCost cost, Frame? parent, long started)
    {
        internal readonly CpuObservationCosts Owner = owner;
        internal readonly CpuObservationCost Cost = cost;
        internal readonly Frame? Parent = parent;
        internal readonly long Started = started;
        internal long ChildTicks;
    }
}
