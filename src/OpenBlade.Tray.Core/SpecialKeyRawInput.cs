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

/// <summary>
/// Reconstructs the function-row actions that PID 02E0 emits directly in
/// Normal mode. In Driver mode the keyboard emits ordinary F-key scan codes
/// for both layers and reports Fn separately, so the session agent must
/// translate only the layer selected for multimedia behavior.
/// </summary>
public static class Rz09_0581FunctionRowMediaInput
{
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

    private static bool TryMap(uint scanCode, out FunctionRowAction action)
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
/// Latches the suppression decision made on the first key-down until the
/// matching key-up. Fn, firmware mode, or service status can change while a
/// key is held without leaking an orphaned F-key edge into the foreground app.
/// One-shot actions ignore auto-repeat; volume and brightness actions repeat.
/// </summary>
public sealed class Rz09_0581FunctionRowInputState
{
    private readonly Dictionary<uint, FunctionRowAction> suppressedKeys = [];

    public bool Process(
        uint scanCode,
        bool keyDown,
        bool functionKeyPressed,
        FunctionKeyPrimaryMode? primaryMode,
        bool driverModeActive,
        out FunctionRowAction? action)
    {
        action = null;
        if (!keyDown)
        {
            return suppressedKeys.Remove(scanCode);
        }

        if (suppressedKeys.TryGetValue(scanCode, out FunctionRowAction repeated))
        {
            if (IsRepeatable(repeated))
            {
                action = repeated;
            }
            return true;
        }

        if (!Rz09_0581FunctionRowMediaInput.TryOverride(
            scanCode,
            keyDown: true,
            functionKeyPressed,
            primaryMode,
            driverModeActive,
            out FunctionRowAction? mapped)
            || mapped is not { } first)
        {
            return false;
        }

        suppressedKeys.Add(scanCode, first);
        action = first;
        return true;
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
    private const byte FunctionReportId = 0x04;
    private const byte KeyboardReportId = 0x01;
    private const byte FunctionPressedValue = 0x0A;
    private const byte M1Usage = 0x4B;
    private const byte M2Usage = 0x4E;
    private const byte M3Usage = 0x03;
    private const byte M4Usage = 0xD3;
    private const byte M5Usage = 0xD4;
    private const int SpecialKeyCount = 5;

    public static bool TryCreateTrigger(
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
