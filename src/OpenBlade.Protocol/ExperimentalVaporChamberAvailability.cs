namespace OpenBlade.Protocol;

/// <summary>
/// Factory-lifetime result of the RZ09-0529 LED38 effect GET (0F/82 region 38).
/// A completed failure is kept for the admitted host and HID identity, so later
/// sessions run keyboard-only frames without repeating a GET that firmware
/// rejected or that poisoned a session. A GET that never reached the MCU, such as
/// a shared-gate refusal or a cancellation, settles nothing, so the next open reads
/// again. A changed identity starts over. This
/// cache only removes the optional probe; every LED38 write still needs its own
/// fresh read, and an LED38 that OpenBlade selected is always read for restore.
/// </summary>
internal sealed class ExperimentalVaporChamberAvailability
{
    private readonly object sync = new();
    private Key? key;
    private bool? available;

    internal readonly record struct Key(HostIdentityEvidence Host, HidHandleIdentity Hid);

    /// <summary>Returns the settled result for this identity, or null when it must be read.</summary>
    internal bool? Get(Key current)
    {
        lock (sync)
        {
            if (key != current)
            {
                key = current;
                available = null;
            }
            return available;
        }
    }

    internal void Settle(Key current, bool result)
    {
        lock (sync)
        {
            if (key == current)
            {
                // A completed failure is final for this identity; a later success
                // from a stale session must not re-enable a rejected GET.
                available = available != false && result;
            }
        }
    }
}
