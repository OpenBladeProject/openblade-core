using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.FanStop.Guardian;

internal sealed record So6120ServiceVolatileAdmission(bool Admitted,
    FanStopEndReason Failure = FanStopEndReason.SafetyCondition, string? Detail = null);

internal sealed record So6120ServiceSafetySnapshot(long Revision, long AdmissionStarted,
    long TemperatureStarted, long Completed, bool Owned, bool Admitted,
    FanTemperatureReadings Reading, TimeSpan SampleAge, bool Canceled,
    FanStopEndReason EndReason, FanStopThermalDecision? ThermalDecision, string? Detail);

// This worker never calls the native fan session or takes its lock. Only this
// worker reads the temperature source and mutates the rolling thermal policy.
// Native command admission consumes one immutable proof, never a second sensor
// read. A failed proof is terminal for the activation, including before entry.
internal sealed class So6120ServiceSafetySampler : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);
    private readonly IFanTemperatureSource source;
    private readonly FanStopSettings settings;
    private readonly TimeProvider clock;
    private readonly Func<So6120ServiceVolatileAdmission> observeAdmission;
    private readonly FanStopThermalPolicy policy;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Func<CancellationToken, Task>? waitNext;
    private readonly TaskCompletionSource initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ownedInitial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private int owned;
    private int disposed;
    private So6120ServiceSafetySnapshot snapshot = new(0, 0, 0, 0, false, false,
        default, default, false, FanStopEndReason.None, null, "No safety observation has completed.");

    internal So6120ServiceSafetySampler(IFanTemperatureSource source, FanStopSettings settings,
        TimeProvider clock, Func<So6120ServiceVolatileAdmission> observeAdmission,
        Func<CancellationToken, Task>? waitNext = null)
    {
        this.source = source;
        this.settings = settings;
        this.clock = clock;
        this.observeAdmission = observeAdmission;
        policy = new(settings, clock);
        this.waitNext = waitNext;
        worker = Task.Run(RunAsync);
    }

    internal So6120ServiceSafetySnapshot Current => Volatile.Read(ref snapshot);
    internal Task Completion => worker;

    internal async Task<So6120ServiceSafetySnapshot> WaitForInitialAsync(CancellationToken token = default)
    {
        try { await initial.Task.WaitAsync(MaximumSampleAge, clock, token).ConfigureAwait(false); }
        catch (Exception) { End(FanStopEndReason.SafetyCondition, "Initial safety observation did not complete within its bound."); }
        return ReadFresh();
    }

    internal async Task<So6120ServiceSafetySnapshot> BeginOwnedStopAsync()
    {
        if (Volatile.Read(ref disposed) != 0) { return ReadFresh(); }
        Interlocked.Exchange(ref owned, 1);
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { return ReadFresh(); }
        // An in-flight entry sample remains an entry sample. Its elapsed time
        // cannot seed either rolling window after this ownership handoff.
        try { await ownedInitial.Task.WaitAsync(MaximumSampleAge, clock).ConfigureAwait(false); }
        catch (Exception) { End(FanStopEndReason.SafetyCondition, "First owned safety observation did not complete within its bound."); }
        return ReadFresh();
    }

    internal So6120ServiceSafetySnapshot ReadFresh()
    {
        while (true)
        {
            So6120ServiceSafetySnapshot current = Current;
            if (current.EndReason != FanStopEndReason.None) { return current; }
            TimeSpan age = clock.GetElapsedTime(current.AdmissionStarted);
            if (Volatile.Read(ref disposed) == 0 && current.Revision != 0 && current.Admitted
                && age >= TimeSpan.Zero && age <= MaximumSampleAge) { return current; }
            var failed = current with
            {
                Admitted = false,
                EndReason = FanStopEndReason.SafetyCondition,
                ThermalDecision = null,
                Detail = "Safety proof is missing, expired, or its sampler is stopped."
            };
            if (Interlocked.CompareExchange(ref snapshot, failed, current) == current) { return failed; }
        }
    }

    internal void End(FanStopEndReason reason, string detail)
    {
        while (true)
        {
            var current = Current;
            if (current.EndReason != FanStopEndReason.None) { return; }
            var failed = current with { Admitted = false, EndReason = reason, ThermalDecision = null, Detail = detail };
            if (Interlocked.CompareExchange(ref snapshot, failed, current) == current) { return; }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested && Current.EndReason == FanStopEndReason.None)
            {
                bool forOwnedStop = Volatile.Read(ref owned) != 0;
                long started = clock.GetTimestamp();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(MaximumSampleAge);
                // Even a synchronously blocked OS reader stays off the native
                // command owner. There is at most one outstanding observation.
                Task<Observation> pending = Task.Run(async () =>
                {
                    So6120ServiceVolatileAdmission admission = observeAdmission();
                    long thermalStarted = clock.GetTimestamp();
                    FanTemperatureReadings reading = default;
                    if (admission.Admitted)
                    {
                        reading = await (settings.EndMode == FanStopEndMode.Custom
                            ? source.ReadForFanStopAsync(deadline.Token) : source.ReadAsync(deadline.Token)).ConfigureAwait(false);
                    }
                    return new Observation(admission, thermalStarted, reading);
                });
                try
                {
                    Observation observation = await pending.WaitAsync(MaximumSampleAge, clock, stop.Token).ConfigureAwait(false);
                    long completed = clock.GetTimestamp();
                    TimeSpan wholeAge = clock.GetElapsedTime(started, completed);
                    TimeSpan thermalAge = clock.GetElapsedTime(observation.TemperatureStarted, completed);
                    bool valid = wholeAge >= TimeSpan.Zero && wholeAge <= MaximumSampleAge && !deadline.IsCancellationRequested;
                    FanStopEndReason reason = FanStopEndReason.None;
                    FanStopThermalDecision? decision = null;
                    bool admitted = false;
                    if (!valid) { reason = FanStopEndReason.SafetyCondition; }
                    else if (!observation.Admission.Admitted) { reason = observation.Admission.Failure; }
                    else
                    {
                        var reading = observation.Reading;
                        admitted = forOwnedStop
                            ? policy.Observe(reading.CpuCelsius, reading.GpuCelsius, reading.GpuInactive,
                                reading.GpuPoweredDown, thermalAge)
                            : policy.AdmitsEntry(reading.CpuCelsius, reading.GpuCelsius, reading.GpuInactive,
                                reading.GpuPoweredDown, thermalAge);
                        decision = forOwnedStop ? policy.TerminalThermalDecision : policy.EntryThermalDecision;
                        if (!admitted)
                        {
                            reason = forOwnedStop ? policy.EndReason
                                : decision is null ? FanStopEndReason.SafetyCondition : FanStopEndReason.ThermalThreshold;
                        }
                    }
                    Publish(new(0, started, observation.TemperatureStarted, completed, forOwnedStop, admitted,
                        observation.Reading, thermalAge, deadline.IsCancellationRequested, reason, decision,
                        observation.Admission.Detail));
                }
                catch (Exception error)
                {
                    if (!stop.IsCancellationRequested)
                    { End(FanStopEndReason.SafetyCondition, $"Safety observation failed ({error.GetType().Name})."); }
                }
                finally
                {
                    initial.TrySetResult();
                    if (forOwnedStop || Current.EndReason != FanStopEndReason.None) { ownedInitial.TrySetResult(); }
                    // A timeout is not completion. Cancel, then retain this one
                    // read and its source until it really returns. Never retry
                    // it or dispose the source underneath its outstanding work.
                    try { deadline.Cancel(); } catch (Exception) { }
                    try { await pending.ConfigureAwait(false); } catch (Exception) { }
                }
                if (Current.EndReason != FanStopEndReason.None) { break; }
                if (waitNext is not null) { await waitNext(stop.Token).ConfigureAwait(false); }
                else
                {
                    // Target start-to-start cadence. Adding a whole second
                    // after a healthy nonzero-duration read creates an expired
                    // proof window while the next read is still in progress.
                    // Overdue reads run immediately, never concurrently; actual
                    // freshness and missing-interval checks remain unchanged.
                    TimeSpan remaining = PollInterval - clock.GetElapsedTime(started);
                    await wake.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { End(FanStopEndReason.SafetyCondition, $"Safety sampler failed ({error.GetType().Name})."); }
        finally
        {
            initial.TrySetResult();
            ownedInitial.TrySetResult();
            try { (source as IDisposable)?.Dispose(); } catch (Exception) { }
        }
    }

    private void Publish(So6120ServiceSafetySnapshot next)
    {
        while (true)
        {
            var current = Current;
            if (current.EndReason != FanStopEndReason.None) { return; }
            next = next with { Revision = current.Revision + 1 };
            if (Interlocked.CompareExchange(ref snapshot, next, current) == current) { return; }
        }
    }

    // Cancellation never waits on native work or an uncooperative sensor. The
    // worker owns final source disposal, after its sole outstanding read ends.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            // User callbacks on a cancellation token may themselves block. Do
            // not put those callbacks on the serialized native cleanup owner.
            Task canceled = stop.CancelAsync();
            _ = DisposeSignalsAfterWorkerAsync(canceled);
        }
    }

    private async Task DisposeSignalsAfterWorkerAsync(Task canceled)
    {
        try { await canceled.ConfigureAwait(false); } catch (Exception) { }
        await worker.ConfigureAwait(false);
        wake.Dispose();
        stop.Dispose();
    }

    internal static Task DisposeSourceAfterAsync(IFanTemperatureSource source, Task outstanding) => Task.Run(async () =>
    {
        try { await outstanding.ConfigureAwait(false); } catch (Exception) { }
        try { (source as IDisposable)?.Dispose(); } catch (Exception) { }
    });

    private sealed record Observation(So6120ServiceVolatileAdmission Admission,
        long TemperatureStarted, FanTemperatureReadings Reading);
}
