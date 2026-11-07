using OpenBlade.Contracts;

namespace OpenBlade.FanStop;

// The policy is immutable for one owned Stop. Only continuation samples advance
// timers; command admission never grants elapsed time or resets a hot sensor.
internal sealed class FanStopThermalPolicy
{
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);
    private readonly FanStopSettings settings;
    private readonly TimeProvider clock;
    private long? cpuAbove, gpuAbove, previousSample;
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

    // This is a freshness/plausibility gate only during an owned Stop. Custom
    // over-limit values are allowed until the independent sampled timer expires.
    internal bool AdmitsCommand(int? cpu, int? gpu, bool gpuInactive, bool gpuPoweredDown,
        TimeSpan sampleAge, bool canceled = false)
    {
        if (ended != FanStopEndReason.None) { return false; }
        FanStopEndReason assessment = Assess(cpu, gpu, gpuInactive, gpuPoweredDown, sampleAge, canceled);
        return assessment == FanStopEndReason.None || End(assessment);
    }

    internal bool Observe(int? cpu, int? gpu, bool gpuInactive, bool gpuPoweredDown,
        TimeSpan sampleAge, bool canceled = false)
    {
        if (ended != FanStopEndReason.None) { return false; }
        long now = clock.GetTimestamp();
        FanStopEndReason assessment = Assess(cpu, gpu, gpuInactive, gpuPoweredDown, sampleAge, canceled);
        if (assessment != FanStopEndReason.None) { return End(assessment); }
        if (settings.EndMode == FanStopEndMode.Auto) { return true; }
        if (previousSample is long previous)
        {
            // The current read already passed its own two-second age bound.
            // Its duration is acquisition work, not a missed observation. Only
            // the uncovered interval before that read can break continuity.
            TimeSpan uncovered = clock.GetElapsedTime(previous, now) - sampleAge;
            if (now < previous || uncovered < TimeSpan.Zero || uncovered > MaximumSampleAge)
            { return End(FanStopEndReason.SafetyCondition); }
        }
        previousSample = now;
        cpuAbove = cpu > settings.CpuLimitCelsius ? cpuAbove ?? now : null;
        gpuAbove = !gpuPoweredDown && gpu > settings.GpuLimitCelsius ? gpuAbove ?? now : null;
        TimeSpan duration = TimeSpan.FromSeconds(settings.SecondsAboveLimit);
        if (cpuAbove is long cpuStart && clock.GetElapsedTime(cpuStart, now) >= duration
            || gpuAbove is long gpuStart && clock.GetElapsedTime(gpuStart, now) >= duration)
        { return End(FanStopEndReason.ThermalThreshold); }
        return true;
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
        return gpuPoweredDown || gpu is >= 10 and <= 125
            ? FanStopEndReason.None : FanStopEndReason.SafetyCondition;
    }

    private bool End(FanStopEndReason reason)
    {
        ended = reason;
        cpuAbove = gpuAbove = null;
        return false;
    }
}
