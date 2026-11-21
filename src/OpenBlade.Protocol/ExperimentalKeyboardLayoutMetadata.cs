namespace OpenBlade.Protocol;

/// <summary>
/// Factory-lifetime result of an exact model's optional keyboard-layout read.
/// The value is a static MCU property, so it is read once for each admitted host
/// and HID identity. A confirmed value and a failure the device answered (a status
/// rejection, an invalid payload, or a GET that poisoned the session) are both kept, so
/// battery, lighting and recovery sessions do not repeat a GET that firmware
/// rejected and that would poison each new session. A refusal before submission, a
/// gate timeout, a no-response timeout and cancellation are not kept. A changed identity starts a
/// fresh read. Every session still passes its own fresh identity and safety
/// admission; this cache only removes repeated metadata reads.
/// </summary>
internal sealed class ExperimentalKeyboardLayoutMetadata
{
    private readonly object sync = new();
    private Key? key;
    private byte? value;
    private bool settled;

    internal readonly record struct Key(HostIdentityEvidence Host, HidHandleIdentity Hid);

    /// <summary>The settled value for the most recently admitted identity.</summary>
    internal byte? Value
    {
        get
        {
            lock (sync)
            {
                return settled ? value : null;
            }
        }
    }

    /// <summary>
    /// Returns true with the settled result when this identity already has one.
    /// Otherwise binds the identity, clears any other identity's result, and
    /// returns false so the caller performs the single read.
    /// </summary>
    internal bool TryGetSettled(Key current, out byte? result)
    {
        lock (sync)
        {
            if (key == current && settled)
            {
                result = value;
                return true;
            }
            key = current;
            value = null;
            settled = false;
            result = null;
            return false;
        }
    }

    /// <summary>Records a confirmed value, or null after a device-answered failed read.</summary>
    internal void Settle(Key current, byte? result)
    {
        lock (sync)
        {
            if (key != current)
            {
                // A newer identity was admitted meanwhile; never attribute this result to it.
                return;
            }
            value = result;
            settled = true;
        }
    }
}
