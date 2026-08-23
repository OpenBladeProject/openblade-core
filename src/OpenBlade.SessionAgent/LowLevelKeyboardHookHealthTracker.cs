namespace OpenBlade.SessionAgent;

/// <summary>
/// Correlates physical low-level-hook events with matching Raw Input packets.
/// Windows can silently remove a timed-out low-level hook,
/// so a target packet without its exact hook observation is the only
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
        uint messageTime)
    {
        lock (sync)
        {
            recent.Add(new ObservedKey(scanCode, keyDown, messageTime));
            if (recent.Count > MaximumRecentEvents)
            {
                recent.RemoveRange(0, recent.Count - MaximumRecentEvents);
            }
        }
    }

    public bool Consume(
        uint scanCode,
        bool keyDown,
        uint messageTime)
    {
        lock (sync)
        {
            int match = recent.FindIndex(item =>
                item.ScanCode == scanCode
                && item.KeyDown == keyDown
                && item.MessageTime == messageTime);
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
        uint MessageTime);
}
