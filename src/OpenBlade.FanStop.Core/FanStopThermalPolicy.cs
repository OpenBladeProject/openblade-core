using OpenBlade.Contracts;

namespace OpenBlade.FanStop;

// The policy is immutable for one owned Stop. Only continuation samples advance
// averaging windows; command admission never grants elapsed time or resets history.
internal sealed class FanStopThermalPolicy
{
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);
    private readonly FanStopSettings settings;
    private readonly TimeProvider clock;
    private long? previousSample;
    private readonly TemperatureWindow cpuWindow = new(), gpuWindow = new();
    private FanStopEndReason ended;

    internal FanStopThermalPolicy(FanStopSettings settings, TimeProvider clock)
    {
        if (!FanStopSettings.IsWellFormed(settings)) { throw new ArgumentException("Invalid Stop thermal policy.", nameof(settings)); }
        this.settings = settings;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal FanStopEndReason EndReason => ended;

    internal static bool IsValidSample(int? cpu, int? gpu, bool gpuPoweredDown,
        TimeSpan age, bool canceled = false) => !canceled && age >= TimeSpan.Zero && age <= MaximumSampleAge
        && cpu is >= 10 and <= 125 && (gpuPoweredDown || gpu is >= 10 and <= 125);

    internal bool AdmitsEntry(int? cpu, int? gpu, bool gpuInactive, bool gpuPoweredDown,
        TimeSpan sampleAge, bool canceled = false) =>
        Assess(cpu, gpu, gpuInactive, gpuPoweredDown, sampleAge, canceled) == FanStopEndReason.None
        && (settings.EndMode == FanStopEndMode.Auto
            || cpu <= settings.CpuLimitCelsius && (gpuPoweredDown || gpu <= settings.GpuLimitCelsius));

    // Command reads enforce freshness and the immediate cutoffs, but cannot
    // change the continuation samples or give an averaging window extra credit.
    internal bool AdmitsCommand(int? cpu, int? gpu, bool gpuInactive, bool gpuPoweredDown,
        TimeSpan sampleAge, bool canceled = false)
    {
        if (ended != FanStopEndReason.None) { return false; }
        FanStopEndReason assessment = Assess(cpu, gpu, gpuInactive, gpuPoweredDown, sampleAge, canceled);
        if (assessment == FanStopEndReason.SafetyCondition || !HasContinuity(clock.GetTimestamp(), sampleAge))
        { return End(FanStopEndReason.SafetyCondition); }
        return assessment == FanStopEndReason.None || End(assessment);
    }

    internal bool Observe(int? cpu, int? gpu, bool gpuInactive, bool gpuPoweredDown,
        TimeSpan sampleAge, bool canceled = false)
    {
        if (ended != FanStopEndReason.None) { return false; }
        long now = clock.GetTimestamp();
        // Continuity faults take priority even when this reading is hot: a
        // thermal completion may rearm automatic entry, a missing interval may not.
        FanStopEndReason assessment = Assess(cpu, gpu, gpuInactive, gpuPoweredDown, sampleAge, canceled);
        if (assessment == FanStopEndReason.SafetyCondition || !HasContinuity(now, sampleAge))
        { return End(FanStopEndReason.SafetyCondition); }
        if (assessment != FanStopEndReason.None) { return End(assessment); }
        if (settings.EndMode == FanStopEndMode.Auto) { return true; }
        long elapsed = previousSample is long last ? clock.GetElapsedTime(last, now).Ticks : 0;
        previousSample = now;
        long duration = TimeSpan.FromSeconds(settings.SecondsAboveLimit).Ticks;
        if (!cpuWindow.Observe(cpu!.Value, elapsed, duration))
        { return End(FanStopEndReason.SafetyCondition); }
        // D3 is an explicit exemption, never a fabricated zero-temperature
        // sample. A waking GPU must build a fresh complete window; CPU history
        // continues independently. Missing D0 temperature already failed Assess.
        if (gpuPoweredDown) { gpuWindow.Clear(); }
        else if (!gpuWindow.Observe(gpu!.Value, elapsed, duration))
        { return End(FanStopEndReason.SafetyCondition); }
        if (cpuWindow.IsAbove(settings.CpuLimitCelsius, duration)
            || !gpuPoweredDown && gpuWindow.IsAbove(settings.GpuLimitCelsius, duration))
        { return End(FanStopEndReason.ThermalThreshold); }
        return true;
    }

    private bool HasContinuity(long now, TimeSpan sampleAge)
    {
        if (settings.EndMode == FanStopEndMode.Auto || previousSample is not long previous) { return true; }
        // Assess has already rejected invalid/canceled/stale reads. Acquisition
        // duration is not a missed observation; only the uncovered interval
        // before it can break continuity. Command admission never advances this
        // timestamp or credits the windows, even when its read is cool.
        if (now < previous) { return false; }
        TimeSpan uncovered = clock.GetElapsedTime(previous, now) - sampleAge;
        return uncovered >= TimeSpan.Zero && uncovered <= MaximumSampleAge;
    }

    private FanStopEndReason Assess(int? cpu, int? gpu, bool gpuInactive, bool gpuPoweredDown,
        TimeSpan age, bool canceled)
    {
        if (canceled || age < TimeSpan.Zero || age > MaximumSampleAge || cpu is not (>= 10 and <= 125))
        { return FanStopEndReason.SafetyCondition; }
        if (settings.EndMode == FanStopEndMode.Auto)
        {
            // Preserve the original CPU-zone/inactive-GPU policy exactly. GPU
            // workload/activity is a safety exit and cannot rearm automation.
            if (!gpuInactive) { return FanStopEndReason.SafetyCondition; }
            return cpu > 55 ? FanStopEndReason.ThermalThreshold : FanStopEndReason.None;
        }
        if (!gpuPoweredDown && gpu is not (>= 10 and <= 125))
        { return FanStopEndReason.SafetyCondition; }
        return cpu > settings.CpuImmediateCutoffCelsius
            || !gpuPoweredDown && gpu > settings.GpuImmediateCutoffCelsius
            ? FanStopEndReason.ThermalThreshold : FanStopEndReason.None;
    }

    private bool End(FanStopEndReason reason)
    {
        ended = reason;
        cpuWindow.Clear();
        gpuWindow.Clear();
        return false;
    }

    // Piecewise-constant integration: each fresh observation holds until the
    // next one. Weight elapsed time, not sample count, and trim the oldest
    // interval exactly at the trailing boundary. Retain a hard capacity bound
    // even for an unexpectedly fast/irregular caller; exhaustion ends Stop
    // safely instead of approximating history or allocating without a bound.
    private sealed class TemperatureWindow
    {
        private const int MaximumSegments = 4096;
        private readonly LinkedList<(long Ticks, int Celsius)> segments = new();
        private int? previous;
        private long coverage, weightedTicks;

        internal bool Observe(int temperature, long elapsed, long duration)
        {
            if (previous is int prior && elapsed > 0)
            {
                if (segments.Last is { } last && last.Value.Celsius == prior)
                { last.Value = (last.Value.Ticks + elapsed, prior); }
                else
                {
                    if (segments.Count >= MaximumSegments) { return false; }
                    segments.AddLast((elapsed, prior));
                }
                coverage += elapsed;
                weightedTicks += elapsed * prior;
                while (coverage > duration && segments.First is { } first)
                {
                    long trim = Math.Min(coverage - duration, first.Value.Ticks);
                    coverage -= trim;
                    weightedTicks -= trim * first.Value.Celsius;
                    if (trim == first.Value.Ticks) { segments.RemoveFirst(); }
                    else { first.Value = (first.Value.Ticks - trim, first.Value.Celsius); }
                }
            }
            previous = temperature;
            return true;
        }

        internal bool IsAbove(int limit, long duration) => coverage == duration && weightedTicks > duration * limit;

        internal void Clear()
        {
            segments.Clear();
            previous = null;
            coverage = weightedTicks = 0;
        }
    }
}
