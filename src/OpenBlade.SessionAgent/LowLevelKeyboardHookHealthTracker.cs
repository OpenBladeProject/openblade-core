namespace OpenBlade.SessionAgent;

/// <summary>
/// Correlates physical low-level-hook events with the matching target-device
/// Raw Input packets. Windows can silently remove a timed-out low-level hook,
/// so a target packet without a recent hook observation is the only
/// non-synthetic signal that the translation hook needs to be reinstated.
/// </summary>
internal sealed class LowLevelKeyboardHookHealthTracker
{
    internal const int MaximumRecentEvents = 64;

    private readonly object sync = new();
    private readonly List<ObservedKey> recent = [];

    public void Record(
        uint scanCode,
        bool keyDown,
        long timestamp)
    {
        lock (sync)
        {
            recent.Add(new ObservedKey(scanCode, keyDown, timestamp));
            if (recent.Count > MaximumRecentEvents)
            {
                recent.RemoveRange(0, recent.Count - MaximumRecentEvents);
            }
        }
    }

    public bool Consume(
        uint scanCode,
        bool keyDown,
        long timestamp,
        long maximumAge)
    {
        if (maximumAge < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        lock (sync)
        {
            long oldestAccepted = timestamp - maximumAge;
            recent.RemoveAll(item => item.Timestamp < oldestAccepted);
            int match = recent.FindIndex(item =>
                item.ScanCode == scanCode
                && item.KeyDown == keyDown
                && item.Timestamp <= timestamp);
            if (match < 0)
            {
                return false;
            }

            recent.RemoveAt(match);
            return true;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            recent.Clear();
        }
    }

    private readonly record struct ObservedKey(
        uint ScanCode,
        bool KeyDown,
        long Timestamp);
}
