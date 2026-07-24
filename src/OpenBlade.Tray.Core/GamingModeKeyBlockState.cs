using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Tracks the small amount of key state needed to suppress Gaming mode
/// shortcuts without leaking an unmatched key-up event to Windows.
/// </summary>
public sealed class GamingModeKeyBlockState
{
    public const uint VirtualKeyTab = 0x09;
    public const uint VirtualKeyAlt = 0x12;
    public const uint VirtualKeyLeftWindows = 0x5B;
    public const uint VirtualKeyRightWindows = 0x5C;
    public const uint VirtualKeyLeftAlt = 0xA4;
    public const uint VirtualKeyRightAlt = 0xA5;

    private bool genericAltPressed;
    private bool leftAltPressed;
    private bool rightAltPressed;
    private bool tabSuppressed;
    private bool leftWindowsSuppressed;
    private bool rightWindowsSuppressed;

    public bool ShouldSuppress(
        uint virtualKey,
        bool keyDown,
        GamingModeKeyPolicy policy,
        bool altContext = false)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (virtualKey is VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt)
        {
            switch (virtualKey)
            {
                case VirtualKeyLeftAlt:
                    leftAltPressed = keyDown;
                    break;
                case VirtualKeyRightAlt:
                    rightAltPressed = keyDown;
                    break;
                default:
                    genericAltPressed = keyDown;
                    break;
            }
            return false;
        }

        if (virtualKey == VirtualKeyLeftWindows)
        {
            return UpdateWindowsKey(
                keyDown,
                policy.BlockWindowsKey,
                ref leftWindowsSuppressed);
        }

        if (virtualKey == VirtualKeyRightWindows)
        {
            return UpdateWindowsKey(
                keyDown,
                policy.BlockWindowsKey,
                ref rightWindowsSuppressed);
        }

        if (virtualKey != VirtualKeyTab)
        {
            return false;
        }

        if (keyDown)
        {
            tabSuppressed = policy.BlockAltTab
                && (altContext
                    || genericAltPressed
                    || leftAltPressed
                    || rightAltPressed);
            return tabSuppressed;
        }

        bool suppressRelease = tabSuppressed;
        tabSuppressed = false;
        return suppressRelease;
    }

    private static bool UpdateWindowsKey(
        bool keyDown,
        bool block,
        ref bool suppressed)
    {
        if (keyDown)
        {
            suppressed = block;
            return suppressed;
        }

        bool suppressRelease = suppressed;
        suppressed = false;
        return suppressRelease;
    }
}
