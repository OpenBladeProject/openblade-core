using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Correlates PID 02C6's Fn HID state with its Windows Raw Keyboard P edges.
/// This path never suppresses keyboard input or admits M1/M2 bindings.
/// </summary>
internal sealed class Rz090528FunctionPInputState
{
    private bool functionPressed;
    private bool pPressed;
    private int resetRequested;

    public void RequestReset() => Interlocked.Exchange(ref resetRequested, 1);

    private void ApplyPendingReset()
    {
        if (Interlocked.Exchange(ref resetRequested, 0) != 0)
        {
            Reset();
        }
    }

    public void Reset()
    {
        functionPressed = false;
        pPressed = false;
    }

    public void ObserveHid(
        RazerBladeInputDeviceKind deviceKind,
        ReadOnlySpan<byte> report,
        bool admitted)
    {
        ApplyPendingReset();
        if (!admitted)
        {
            Reset();
            return;
        }
        if (deviceKind != RazerBladeInputDeviceKind.Rz09_0528
            || report.Length < 3
            || report[0] != 0x04)
        {
            return;
        }

        functionPressed = report[1] == 0x0A;
        // Keep the physical P edge latched until key-up, even if Fn is
        // released and pressed again while P is held.
    }

    public bool TryCreateTrigger(
        RazerBladeInputDeviceKind deviceKind,
        ushort makeCode,
        ushort flags,
        ushort virtualKey,
        bool admitted)
    {
        ApplyPendingReset();
        if (!admitted)
        {
            Reset();
            return false;
        }
        if (deviceKind != RazerBladeInputDeviceKind.Rz09_0528
            || makeCode != 0x19
            || virtualKey != 0x50
            || (flags & ~0x01) != 0)
        {
            return false;
        }

        bool down = (flags & 0x01) == 0;
        bool trigger = down && !pPressed && functionPressed;
        pPressed = down;
        return trigger;
    }
}
