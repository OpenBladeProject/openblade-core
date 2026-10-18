using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal enum Blade18DefaultFnAction
{
    VolumeMute,
    VolumeDown,
    VolumeUp,
    Project,
    PreviousTrack,
    PlayPause,
    NextTrack,
    DisplayBrightnessDown,
    DisplayBrightnessUp,
    KeyboardBrightnessDown,
    KeyboardBrightnessUp,
    PrintScreen,
    PageUp,
    Home,
    PageDown,
    End,
    Insert,
    Performance,
    RefreshRate,
    Touchpad,
}

internal readonly record struct Blade18DefaultFnInputEvent(
    uint ScanCode,
    Blade18DefaultFnAction Action,
    FunctionRowInputPhase Phase);

internal sealed class Blade18DefaultFnInputState
{
    private readonly Dictionary<KeyIdentity, ActivePress> activePresses = [];

    public bool Process(
        uint scanCode,
        bool extended,
        bool keyDown,
        bool keyWasAlreadyDown,
        bool functionHeld,
        bool admitted,
        out Blade18DefaultFnInputEvent? inputEvent)
    {
        inputEvent = null;
        var key = new KeyIdentity(scanCode, extended);

        if (!keyDown)
        {
            if (!activePresses.Remove(key, out ActivePress released))
            {
                return false;
            }

            if (released.Consume && !released.Canceled)
            {
                inputEvent = new(scanCode, released.Action, FunctionRowInputPhase.Up);
            }

            return released.Consume;
        }

        if (!keyWasAlreadyDown)
        {
            // A fresh physical down proves any old latch for this identity is
            // stale. Choose again from the current Fn and admission state.
            activePresses.Remove(key);
        }
        else if (activePresses.TryGetValue(key, out ActivePress repeated))
        {
            if (repeated.Consume
                && !repeated.Canceled
                && IsRepeatable(repeated.Action))
            {
                inputEvent = new(scanCode, repeated.Action, FunctionRowInputPhase.Repeat);
            }

            return repeated.Consume;
        }

        if (!TryMap(scanCode, extended, out Blade18DefaultFnAction action))
        {
            return false;
        }

        // When the first observed edge is already held, its initial decision
        // is unknown. Latch pass-through so pressing Fn later cannot activate
        // a shortcut in the middle of that physical press.
        bool consume = !keyWasAlreadyDown && functionHeld && admitted;
        activePresses.Add(key, new(action, consume, Canceled: false));
        if (consume)
        {
            inputEvent = new(scanCode, action, FunctionRowInputPhase.Down);
        }

        return consume;
    }

    public bool MaySuppress(
        uint scanCode, bool extended, bool keyDown, bool keyWasAlreadyDown,
        bool functionHeld, bool admitted) =>
        (activePresses.TryGetValue(new(scanCode, extended), out ActivePress press) && press.Consume)
        || (keyDown && !keyWasAlreadyDown && functionHeld && admitted
            && TryMap(scanCode, extended, out _));

    public void PassThroughAllPresses()
    {
        // Changed topology makes every global hook latch ambiguous. Retain
        // pass-through decisions so restoring topology cannot activate held keys.
        foreach (KeyIdentity key in activePresses.Keys.ToArray())
        {
            activePresses[key] = activePresses[key] with { Consume = false, Canceled = true };
        }
    }

    public void Cancel()
    {
        foreach (KeyIdentity key in activePresses.Keys.ToArray())
        {
            ActivePress press = activePresses[key];
            if (press.Consume)
            {
                activePresses[key] = press with { Canceled = true };
            }
        }
    }

    public void CancelPress(uint scanCode, bool extended)
    {
        var key = new KeyIdentity(scanCode, extended);
        if (activePresses.TryGetValue(key, out ActivePress press)
            && press.Consume)
        {
            activePresses[key] = press with { Canceled = true };
        }
    }

    public bool ReconcilePassedThroughEdge(
        uint scanCode,
        bool extended,
        bool keyDown,
        out Blade18DefaultFnInputEvent? inputEvent)
    {
        inputEvent = null;
        if (!TryMap(scanCode, extended, out Blade18DefaultFnAction action))
        {
            return false;
        }

        var key = new KeyIdentity(scanCode, extended);
        if (keyDown)
        {
            // The physical edge already reached its foreground target. Drop
            // any prior decision and latch pass-through for the remainder of
            // this press so a later hook repeat cannot start an action.
            activePresses[key] = new(action, Consume: false, Canceled: false);
            return true;
        }

        if (activePresses.Remove(key, out ActivePress released)
            && released.Consume
            && !released.Canceled)
        {
            inputEvent = new(scanCode, released.Action, FunctionRowInputPhase.Up);
        }

        return true;
    }

    private static bool TryMap(
        uint scanCode,
        bool extended,
        out Blade18DefaultFnAction action)
    {
        action = (scanCode, extended) switch
        {
            (0x19, false) => Blade18DefaultFnAction.Performance,
            (0x13, false) => Blade18DefaultFnAction.RefreshRate,
            (0x14, false) => Blade18DefaultFnAction.Touchpad,
            (0x3B, false) => Blade18DefaultFnAction.VolumeMute,
            (0x3C, false) => Blade18DefaultFnAction.VolumeDown,
            (0x3D, false) => Blade18DefaultFnAction.VolumeUp,
            (0x3E, false) => Blade18DefaultFnAction.Project,
            (0x3F, false) => Blade18DefaultFnAction.PreviousTrack,
            (0x40, false) => Blade18DefaultFnAction.PlayPause,
            (0x41, false) => Blade18DefaultFnAction.NextTrack,
            (0x42, false) => Blade18DefaultFnAction.DisplayBrightnessDown,
            (0x43, false) => Blade18DefaultFnAction.DisplayBrightnessUp,
            (0x44, false) => Blade18DefaultFnAction.KeyboardBrightnessDown,
            (0x57, false) => Blade18DefaultFnAction.KeyboardBrightnessUp,
            (0x58, false) => Blade18DefaultFnAction.PrintScreen,
            (0x48, true) => Blade18DefaultFnAction.PageUp,
            (0x4B, true) => Blade18DefaultFnAction.Home,
            (0x50, true) => Blade18DefaultFnAction.PageDown,
            (0x4D, true) => Blade18DefaultFnAction.End,
            (0x53, true) => Blade18DefaultFnAction.Insert,
            _ => default,
        };

        return (scanCode, extended) is
            (0x19, false) or
            (0x13, false) or
            (0x14, false) or
            ( >= 0x3B and <= 0x44, false) or
            (0x57, false) or
            (0x58, false) or
            (0x48, true) or
            (0x4B, true) or
            (0x50, true) or
            (0x4D, true) or
            (0x53, true);
    }

    private static bool IsRepeatable(Blade18DefaultFnAction action) => action is
        Blade18DefaultFnAction.VolumeDown or
        Blade18DefaultFnAction.VolumeUp or
        Blade18DefaultFnAction.DisplayBrightnessDown or
        Blade18DefaultFnAction.DisplayBrightnessUp or
        Blade18DefaultFnAction.KeyboardBrightnessDown or
        Blade18DefaultFnAction.KeyboardBrightnessUp or
        Blade18DefaultFnAction.PageUp or
        Blade18DefaultFnAction.Home or
        Blade18DefaultFnAction.PageDown or
        Blade18DefaultFnAction.End or
        Blade18DefaultFnAction.Insert;

    private readonly record struct KeyIdentity(uint ScanCode, bool Extended);

    private readonly record struct ActivePress(
        Blade18DefaultFnAction Action,
        bool Consume,
        bool Canceled);
}
