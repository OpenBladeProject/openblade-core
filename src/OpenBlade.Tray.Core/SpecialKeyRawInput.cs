using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum FunctionRowAction
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
}

public enum FunctionRowInputPhase
{
    Down,
    Repeat,
    Up,
}

public readonly record struct FunctionRowInputEvent(
    uint ScanCode,
    FunctionRowAction Action,
    FunctionRowInputPhase Phase);

/// <summary>
/// Reconstructs function-row actions for exact devices that emit ordinary
/// F-key scan codes for both layers in Driver mode and report Fn separately.
/// Device support is explicit so a newly admitted model cannot inherit this
/// behavior from layout similarity alone.
/// </summary>
public static class RazerBladeFunctionRowMediaInput
{
    public static bool SupportsDevice(RazerBladeInputDeviceKind deviceKind) =>
        deviceKind is
            RazerBladeInputDeviceKind.Rz09_0581 or
            RazerBladeInputDeviceKind.Rz09_0528;

    public static bool TryOverride(
        uint scanCode,
        bool keyDown,
        bool functionKeyPressed,
        FunctionKeyPrimaryMode? primaryMode,
        bool driverModeActive,
        out FunctionRowAction? action)
    {
        action = null;
        if (!driverModeActive || primaryMode is null)
        {
            return false;
        }

        bool mediaLayerActive = primaryMode == FunctionKeyPrimaryMode.MultimediaKeys
            ? !functionKeyPressed
            : functionKeyPressed;
        if (!mediaLayerActive
            || !TryMap(scanCode, out FunctionRowAction mapped))
        {
            return false;
        }

        if (keyDown)
        {
            action = mapped;
        }
        return true;
    }

    internal static bool TryMap(uint scanCode, out FunctionRowAction action)
    {
        action = scanCode switch
        {
            0x3B => FunctionRowAction.VolumeMute,
            0x3C => FunctionRowAction.VolumeDown,
            0x3D => FunctionRowAction.VolumeUp,
            0x3E => FunctionRowAction.Project,
            0x3F => FunctionRowAction.PreviousTrack,
            0x40 => FunctionRowAction.PlayPause,
            0x41 => FunctionRowAction.NextTrack,
            0x42 => FunctionRowAction.DisplayBrightnessDown,
            0x43 => FunctionRowAction.DisplayBrightnessUp,
            0x44 => FunctionRowAction.KeyboardBrightnessDown,
            0x57 => FunctionRowAction.KeyboardBrightnessUp,
            0x58 => FunctionRowAction.PrintScreen,
            _ => default,
        };
        return scanCode is >= 0x3B and <= 0x44 or 0x57 or 0x58;
    }
}

/// <summary>
/// Latches the suppression or pass-through decision made on the first key-down
/// until the matching key-up. Fn, firmware mode, or service status can change
/// while a key is held without mixing F-key and media behavior for that press.
/// One-shot actions ignore auto-repeat; volume and brightness actions repeat.
/// Every suppressed key publishes its matching release so consumers can
/// safely hold native input state across changes to Fn or service status.
/// </summary>
public sealed class RazerBladeFunctionRowInputState
{
    private readonly Dictionary<uint, FunctionRowAction> suppressedKeys = [];
    private readonly HashSet<uint> passedThroughKeys = [];
    private readonly HashSet<uint> passThroughReleases = [];

    public bool Process(
        uint scanCode,
        bool keyDown,
        bool functionKeyPressed,
        FunctionKeyPrimaryMode? primaryMode,
        bool driverModeActive,
        out FunctionRowInputEvent? inputEvent) => ProcessCore(
            deviceSupported: true,
            scanCode,
            keyDown,
            functionKeyPressed,
            primaryMode,
            driverModeActive,
            out inputEvent);

    public bool Process(
        RazerBladeInputDeviceKind deviceKind,
        uint scanCode,
        bool keyDown,
        bool functionKeyPressed,
        FunctionKeyPrimaryMode? primaryMode,
        bool driverModeActive,
        out FunctionRowInputEvent? inputEvent) => ProcessCore(
            RazerBladeFunctionRowMediaInput.SupportsDevice(deviceKind),
            scanCode,
            keyDown,
            functionKeyPressed,
            primaryMode,
            driverModeActive,
            out inputEvent);

    private bool ProcessCore(
        bool deviceSupported,
        uint scanCode,
        bool keyDown,
        bool functionKeyPressed,
        FunctionKeyPrimaryMode? primaryMode,
        bool driverModeActive,
        out FunctionRowInputEvent? inputEvent)
    {
        inputEvent = null;
        if (!keyDown)
        {
            if (!suppressedKeys.Remove(scanCode, out FunctionRowAction released))
            {
                passedThroughKeys.Remove(scanCode);
                passThroughReleases.Remove(scanCode);
                return false;
            }

            inputEvent = new(scanCode, released, FunctionRowInputPhase.Up);
            return !passThroughReleases.Remove(scanCode);
        }

        if (suppressedKeys.TryGetValue(scanCode, out FunctionRowAction repeated))
        {
            if (IsRepeatable(repeated))
            {
                inputEvent = new(scanCode, repeated, FunctionRowInputPhase.Repeat);
            }
            return true;
        }

        if (passedThroughKeys.Contains(scanCode))
        {
            return false;
        }

        if (!deviceSupported
            || !RazerBladeFunctionRowMediaInput.TryOverride(
            scanCode,
            keyDown: true,
            functionKeyPressed,
            primaryMode,
            driverModeActive,
            out FunctionRowAction? mapped)
            || mapped is not { } first)
        {
            if (RazerBladeFunctionRowMediaInput.TryMap(scanCode, out _))
            {
                passedThroughKeys.Add(scanCode);
            }

            return false;
        }

        suppressedKeys.Add(scanCode, first);
        inputEvent = new(scanCode, first, FunctionRowInputPhase.Down);
        return true;
    }

    /// <summary>
    /// Reconciles a target-device edge without a matching recent hook
    /// observation. The edge may already have reached the foreground
    /// application, so a new media action must never be started or repeated
    /// here. A release for an action suppressed on an earlier edge is still
    /// published so held native state can be reset.
    /// </summary>
    public bool ReconcilePassedThroughEdge(
        uint scanCode,
        bool keyDown,
        out FunctionRowInputEvent? releaseEvent)
    {
        releaseEvent = null;
        if (suppressedKeys.TryGetValue(scanCode, out FunctionRowAction suppressed))
        {
            // Correlation is deliberately bounded, so an uncorrelated repeat
            // does not prove that the hook missed it. Preserve the original
            // suppression decision for later repeats. Let the eventual release
            // through in case this edge escaped after a genuine hook loss.
            if (!keyDown)
            {
                suppressedKeys.Remove(scanCode);
                passThroughReleases.Remove(scanCode);
                releaseEvent = new(scanCode, suppressed, FunctionRowInputPhase.Up);
            }
            else
            {
                passThroughReleases.Add(scanCode);
            }

            return true;
        }

        bool shouldRecover = RazerBladeFunctionRowMediaInput.TryMap(scanCode, out _);
        if (!keyDown)
        {
            passedThroughKeys.Remove(scanCode);
            return shouldRecover;
        }

        if (shouldRecover)
        {
            passedThroughKeys.Add(scanCode);
        }

        return shouldRecover;
    }

    private static bool IsRepeatable(FunctionRowAction action) => action is
        FunctionRowAction.VolumeDown or
        FunctionRowAction.VolumeUp or
        FunctionRowAction.DisplayBrightnessDown or
        FunctionRowAction.DisplayBrightnessUp or
        FunctionRowAction.KeyboardBrightnessDown or
        FunctionRowAction.KeyboardBrightnessUp;
}

/// <summary>
/// Recognizes the five Fn-layer reports captured from the RZ09-0581 MI_01
/// collection. It emits only a logical key identity and keeps no text,
/// foreground-window, or application history.
/// </summary>
public static class Rz09_0581SpecialKeyRawHidInput
{
    public static bool TryCreateTrigger(
        ReadOnlySpan<byte> report,
        ref bool functionKeyPressed,
        Span<bool> keyPressed,
        out SpecialKeySlot slot) =>
        RazerBladeFnLayerSpecialKeyInput.TryCreateTrigger(
            report,
            ref functionKeyPressed,
            keyPressed,
            out slot);
}

/// <summary>
/// Recognizes the exact PID 02C6 Fn-layer sequences. The packet shapes match
/// PID 02E0, but callers must still select this decoder from the resolved
/// device identity so the PID 02E0 D3 performance-button interpretation never
/// runs for the RZ09-0528 M4 packet.
/// </summary>
public static class Rz09_0528SpecialKeyRawHidInput
{
    public static bool TryCreateTrigger(
        ReadOnlySpan<byte> report,
        ref bool functionKeyPressed,
        Span<bool> keyPressed,
        out SpecialKeySlot slot) =>
        RazerBladeFnLayerSpecialKeyInput.TryCreateTrigger(
            report,
            ref functionKeyPressed,
            keyPressed,
            out slot);
}

public static class RazerBladeSpecialKeyRawHidInput
{
    public static bool TryCreateTrigger(
        RazerBladeInputDeviceKind deviceKind,
        ReadOnlySpan<byte> report,
        ref bool functionKeyPressed,
        Span<bool> keyPressed,
        out SpecialKeySlot slot) =>
        deviceKind switch
        {
            RazerBladeInputDeviceKind.Rz09_0581 =>
                Rz09_0581SpecialKeyRawHidInput.TryCreateTrigger(
                    report,
                    ref functionKeyPressed,
                    keyPressed,
                    out slot),
            RazerBladeInputDeviceKind.Rz09_0528 =>
                Rz09_0528SpecialKeyRawHidInput.TryCreateTrigger(
                    report,
                    ref functionKeyPressed,
                    keyPressed,
                    out slot),
            _ => NoTrigger(out slot),
        };

    private static bool NoTrigger(out SpecialKeySlot slot)
    {
        slot = default;
        return false;
    }
}

internal static class RazerBladeFnLayerSpecialKeyInput
{
    private const byte FunctionReportId = 0x04;
    private const byte KeyboardReportId = 0x01;
    private const byte FunctionPressedValue = 0x0A;
    private const byte M1Usage = 0x4B;
    private const byte M2Usage = 0x4E;
    private const byte M3Usage = 0x03;
    private const byte M4Usage = 0xD3;
    private const byte M5Usage = 0xD4;
    private const int SpecialKeyCount = 5;

    internal static bool TryCreateTrigger(
        ReadOnlySpan<byte> report,
        ref bool functionKeyPressed,
        Span<bool> keyPressed,
        out SpecialKeySlot slot)
    {
        slot = default;
        if (report.Length < 3
            || keyPressed.Length < SpecialKeyCount)
        {
            return false;
        }

        if (report[0] == FunctionReportId)
        {
            functionKeyPressed = report[1] == FunctionPressedValue;
            SpecialKeySlot? current = functionKeyPressed
                ? report[2] switch
                {
                    M3Usage => SpecialKeySlot.M3,
                    M4Usage => SpecialKeySlot.M4,
                    M5Usage => SpecialKeySlot.M5,
                    _ => null,
                }
                : null;
            return UpdatePressedState(current, keyPressed, out slot);
        }

        if (report[0] == KeyboardReportId)
        {
            SpecialKeySlot? current = functionKeyPressed
                ? report[2] switch
                {
                    M1Usage => SpecialKeySlot.M1,
                    M2Usage => SpecialKeySlot.M2,
                    _ => null,
                }
                : null;
            return UpdatePressedState(current, keyPressed, out slot);
        }

        return false;
    }

    private static bool UpdatePressedState(
        SpecialKeySlot? current,
        Span<bool> keyPressed,
        out SpecialKeySlot slot)
    {
        bool trigger = false;
        slot = default;
        for (int index = 0; index < SpecialKeyCount; index++)
        {
            SpecialKeySlot candidate = (SpecialKeySlot)index;
            bool now = current == candidate;
            if (now && !keyPressed[index])
            {
                trigger = true;
                slot = candidate;
            }
            keyPressed[index] = now;
        }

        return trigger;
    }
}

/// <summary>
/// Recognizes the non-Fn M3 and M5 reports that retain the laptop's normal
/// Game mode and microphone-mute behavior while keyboard Driver mode is active.
/// These reports are deliberately kept separate from the customizable Fn-layer
/// M3 and M5 identities above.
/// </summary>
public static class Rz09_0581DefaultLaptopKeyRawHidInput
{
    private const byte FunctionReportId = 0x04;
    private const byte GamingModeUsage = 0x03;
    private const byte MicrophoneMuteUsage = 0xD4;

    public static bool TryCreateTrigger(
        ReadOnlySpan<byte> report,
        ref bool gamingModePressed,
        ref bool microphoneMutePressed,
        out SpecialKeySlot slot)
    {
        slot = default;
        if (report.Length < 3 || report[0] != FunctionReportId)
        {
            return false;
        }

        bool gamingModeNow = report[1] == GamingModeUsage;
        bool microphoneMuteNow = report[1] == MicrophoneMuteUsage;
        bool gamingModeTrigger = gamingModeNow && !gamingModePressed;
        bool microphoneMuteTrigger =
            microphoneMuteNow && !microphoneMutePressed;
        gamingModePressed = gamingModeNow;
        microphoneMutePressed = microphoneMuteNow;

        if (gamingModeTrigger)
        {
            slot = SpecialKeySlot.M3;
            return true;
        }

        if (microphoneMuteTrigger)
        {
            slot = SpecialKeySlot.M5;
            return true;
        }

        return false;
    }
}
