namespace OpenBlade.Tray;

public sealed class TrayHoverTelemetryRefreshGate(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private DateTimeOffset nextRefreshAt = DateTimeOffset.MinValue;

    public bool TryAcquire(TimeSpan configuredInterval)
    {
        if (configuredInterval == Timeout.InfiniteTimeSpan)
        {
            return false;
        }

        TimeSpan interval = configuredInterval < MinimumRefreshInterval
            ? MinimumRefreshInterval
            : configuredInterval;
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (now < nextRefreshAt)
            {
                return false;
            }

            nextRefreshAt = now + interval;
            return true;
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            nextRefreshAt = DateTimeOffset.MinValue;
        }
    }
}
