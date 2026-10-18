using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Shared timestamp handling for already-admitted key-triggered renderers.</summary>
internal static class LightingKeyPulseTiming
{
    private static readonly TimeSpan MaximumKeyPulsePreAge = TimeSpan.FromMilliseconds(75);

    internal static TimeSpan ResolveElapsed(
        ReactiveKeyPulse pulse,
        long effectStartedTimestamp,
        TimeSpan fallbackElapsed,
        TimeProvider timeProvider)
    {
        if (pulse.ObservedTimestamp is not { } observedTimestamp)
        {
            return fallbackElapsed;
        }

        if (observedTimestamp <= effectStartedTimestamp)
        {
            return fallbackElapsed;
        }

        TimeSpan observedElapsed;
        try
        {
            observedElapsed = timeProvider.GetElapsedTime(
                effectStartedTimestamp,
                observedTimestamp);
        }
        catch
        {
            return fallbackElapsed;
        }

        if (observedElapsed < TimeSpan.Zero)
        {
            return fallbackElapsed;
        }

        if (observedElapsed >= fallbackElapsed)
        {
            return fallbackElapsed;
        }

        TimeSpan earliestVisibleStart = fallbackElapsed > MaximumKeyPulsePreAge
            ? fallbackElapsed - MaximumKeyPulsePreAge
            : TimeSpan.Zero;
        return observedElapsed < earliestVisibleStart
            ? earliestVisibleStart
            : observedElapsed;
    }

}
