using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Recognizes the exact RZ09-0581 M1 and M2 keyboard packets that Windows Raw
/// Input exposes instead of the report-ID-01 HID packets present in USB
/// captures. The shared pressed state also prevents a duplicate trigger if a
/// future Windows input path exposes both representations.
/// </summary>
internal static class SpecialKeyRawKeyboardState
{
    private const int SpecialKeyCount = 5;

    public static bool TryCreateTrigger(
        RazerBladeInputDeviceKind deviceKind,
        uint virtualKey,
        bool keyDown,
        bool functionKeyPressed,
        Span<bool> keyPressed,
        out SpecialKeySlot slot)
    {
        slot = default;
        if (keyPressed.Length < SpecialKeyCount
            || !SpecialKeyKeyboardSuppressionState.TryMap(virtualKey, out slot))
        {
            return false;
        }

        int index = (int)slot;
        if (!keyDown
            || !functionKeyPressed
            || deviceKind != RazerBladeInputDeviceKind.Rz09_0581)
        {
            keyPressed[index] = false;
            return false;
        }

        bool trigger = !keyPressed[index];
        keyPressed[index] = true;
        return trigger;
    }
}
