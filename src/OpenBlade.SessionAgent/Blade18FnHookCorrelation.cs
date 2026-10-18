namespace OpenBlade.SessionAgent;

/// <summary>
/// Defers Blade 18 Fn actions until Raw Input identifies the physical keyboard
/// that produced the matching low-level-hook edge.
/// </summary>
internal sealed class Blade18FnHookCorrelation
{
    internal const int MaximumRecentEvents = 64;

    private readonly object sync = new();
    private readonly List<ObservedKey> recent = [];

    public bool Record(
        uint scanCode,
        bool extended,
        bool keyDown,
        uint messageTime,
        Blade18DefaultFnInputEvent? inputEvent)
    {
        lock (sync)
        {
            recent.Add(new(scanCode, extended, keyDown, messageTime, inputEvent));
            if (recent.Count > MaximumRecentEvents)
            {
                recent.Clear();
                return false;
            }
            return true;
        }
    }

    public bool TryConsume(
        uint scanCode,
        bool extended,
        bool keyDown,
        uint messageTime,
        out Blade18DefaultFnInputEvent? inputEvent)
    {
        lock (sync)
        {
            int match = recent.FindIndex(item =>
                item.ScanCode == scanCode
                && item.Extended == extended
                && item.KeyDown == keyDown
                && item.MessageTime == messageTime);
            if (match < 0)
            {
                inputEvent = null;
                return false;
            }

            inputEvent = recent[match].InputEvent;
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
        bool Extended,
        bool KeyDown,
        uint MessageTime,
        Blade18DefaultFnInputEvent? InputEvent);
}
