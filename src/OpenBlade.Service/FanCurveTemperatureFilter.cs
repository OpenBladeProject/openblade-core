namespace OpenBlade.Service;

/// <summary>
/// Shared temperature conditioning for host-driven fan curves (laptop manual curve and cooling pad).
/// Each sensor is exponentially smoothed over the actual elapsed time, then a falling-only
/// temperature deadband holds the value until the smoothed reading drops clearly below it.
/// This is purely temperature-domain: there is no timed cooldown hold. Callers keep every
/// safety decision (missing sensors, emergency limits) on the raw readings.
/// </summary>
internal sealed class FanCurveTemperatureFilter(TimeProvider clock)
{
    // About 5 s: a one- or two-sample package spike moves the output a few degrees, while a
    // sustained step is followed to within about 1 degree after 15 s.
    internal static readonly TimeSpan TimeConstant = TimeSpan.FromSeconds(5);
    // Samples further apart than this are unrelated; the next one seeds the filter again.
    internal static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(10);
    // Falling temperatures must drop this far below the held value before the output follows.
    internal const double FallingDeadbandCelsius = 3;

    private readonly object sync = new();
    private Channel cpu;
    private Channel gpu;
    private long? lastSample;

    internal void Reset()
    {
        lock (sync)
        {
            cpu = default;
            gpu = default;
            lastSample = null;
        }
    }

    /// <summary>
    /// Adds one raw sample and returns the conditioned readings. A missing sensor is returned
    /// as missing and its state is discarded, so a returning sensor seeds from its first sample.
    /// Device-power flags pass through unchanged.
    /// </summary>
    internal FanTemperatureReadings Filter(FanTemperatureReadings raw)
    {
        lock (sync)
        {
            long now = clock.GetTimestamp();
            double? elapsedSeconds = null;
            if (lastSample is { } previous)
            {
                TimeSpan elapsed = clock.GetElapsedTime(previous, now);
                if (elapsed < TimeSpan.Zero || elapsed > MaximumSampleGap)
                {
                    cpu = default;
                    gpu = default;
                }
                else
                {
                    elapsedSeconds = elapsed.TotalSeconds;
                }
            }
            lastSample = now;
            return raw with
            {
                CpuCelsius = cpu.Update(raw.CpuCelsius, elapsedSeconds),
                GpuCelsius = gpu.Update(raw.GpuCelsius, elapsedSeconds),
            };
        }
    }

    private struct Channel
    {
        private bool seeded;
        private double smoothed;
        private double held;

        internal int? Update(int? raw, double? elapsedSeconds)
        {
            if (raw is not int value)
            {
                this = default;
                return null;
            }
            if (!seeded || elapsedSeconds is not double seconds)
            {
                seeded = true;
                smoothed = value;
                held = value;
                return value;
            }
            double alpha = 1 - Math.Exp(-seconds / TimeConstant.TotalSeconds);
            smoothed += alpha * (value - smoothed);
            if (smoothed > held)
            {
                held = smoothed;
            }
            else if (smoothed < held - FallingDeadbandCelsius)
            {
                held = smoothed + FallingDeadbandCelsius;
            }
            return (int)Math.Round(held, MidpointRounding.AwayFromZero);
        }
    }
}
