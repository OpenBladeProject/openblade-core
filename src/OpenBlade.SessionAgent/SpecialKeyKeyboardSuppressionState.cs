using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Suppresses the ordinary Page Up or Page Down keyboard edge emitted for an
/// admitted RZ09-0581 M1 or M2 press when a custom binding owns that key. The
/// first key-down decision is retained through key-up so a mid-press binding,
/// Fn, or device-state change cannot leak an unmatched edge.
/// </summary>
internal sealed class SpecialKeyKeyboardSuppressionState
{
    internal const uint VirtualKeyPageUp = 0x21;
    internal const uint VirtualKeyPageDown = 0x22;

    private readonly Dictionary<SpecialKeySlot, bool> suppressionBySlot = [];

    public bool ShouldSuppress(
        uint virtualKey,
        bool keyDown,
        bool functionKeyPressed,
        RazerBladeInputDeviceKind deviceKind,
        Func<SpecialKeySlot, bool> isInputAdmitted,
        Func<SpecialKeySlot, bool> shouldOverride) => ShouldSuppress(
            virtualKey,
            keyDown,
            functionKeyPressed,
            deviceKind,
            isInputAdmitted,
            shouldOverride,
            out _);

    public bool ShouldSuppress(
        uint virtualKey,
        bool keyDown,
        bool functionKeyPressed,
        RazerBladeInputDeviceKind deviceKind,
        Func<SpecialKeySlot, bool> isInputAdmitted,
        Func<SpecialKeySlot, bool> shouldOverride,
        out SpecialKeySlot? triggeredSlot)
    {
        ArgumentNullException.ThrowIfNull(isInputAdmitted);
        ArgumentNullException.ThrowIfNull(shouldOverride);
        triggeredSlot = null;

        if (!TryMap(virtualKey, out SpecialKeySlot slot))
        {
            return false;
        }

        if (!keyDown)
        {
            return suppressionBySlot.Remove(slot, out bool suppressed)
                && suppressed;
        }

        if (suppressionBySlot.TryGetValue(slot, out bool existing))
        {
            return existing;
        }

        bool suppress = deviceKind == RazerBladeInputDeviceKind.Rz09_0581
            && functionKeyPressed
            && isInputAdmitted(slot)
            && shouldOverride(slot);
        suppressionBySlot.Add(slot, suppress);
        if (suppress)
        {
            triggeredSlot = slot;
        }
        return suppress;
    }

    internal static bool TryMap(uint virtualKey, out SpecialKeySlot slot)
    {
        slot = virtualKey switch
        {
            VirtualKeyPageUp => SpecialKeySlot.M1,
            VirtualKeyPageDown => SpecialKeySlot.M2,
            _ => default,
        };
        return virtualKey is VirtualKeyPageUp or VirtualKeyPageDown;
    }
}
