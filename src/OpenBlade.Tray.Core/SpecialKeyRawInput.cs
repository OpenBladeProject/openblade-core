namespace OpenBlade.Tray;

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
