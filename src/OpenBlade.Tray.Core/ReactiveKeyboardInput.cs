using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Maps Windows scan-code make events to the physical matrix slots confirmed
/// for the RZ09-0581. It never accepts translated text,
/// virtual-key values, window titles, or foreground-process information.
/// </summary>
public static class Rz09_0581ReactiveKeyMap
{
    private static readonly IReadOnlyDictionary<ushort, byte> StandardScanCodes =
        new Dictionary<ushort, byte>
        {
            [0x01] = 1,  // Escape

            [0x3B] = 2,  // F1
            [0x3C] = 3,  // F2
            [0x3D] = 4,  // F3
            [0x3E] = 5,  // F4
            [0x3F] = 6,  // F5
            [0x40] = 7,  // F6
            [0x41] = 8,  // F7
            [0x42] = 9,  // F8
            [0x43] = 10, // F9
            [0x44] = 11, // F10
            [0x57] = 12, // F11
            [0x58] = 13, // F12

            [0x29] = 18, // `
            [0x02] = 19, // 1
            [0x03] = 20, // 2
            [0x04] = 21, // 3
            [0x05] = 22, // 4
            [0x06] = 23, // 5
            [0x07] = 24, // 6
            [0x08] = 25, // 7
            [0x09] = 26, // 8
            [0x0A] = 27, // 9
            [0x0B] = 28, // 0
            [0x0C] = 29, // -
            [0x0D] = 30, // =
            [0x0E] = 32, // Backspace

            [0x0F] = 35, // Tab
            [0x10] = 36, // Q
            [0x11] = 37, // W
            [0x12] = 38, // E
            [0x13] = 39, // R
            [0x14] = 40, // T
            [0x15] = 41, // Y
            [0x16] = 42, // U
            [0x17] = 43, // I
            [0x18] = 44, // O
            [0x19] = 45, // P
            [0x1A] = 46, // [
            [0x1B] = 47, // ]
            [0x2B] = 49, // \

            [0x3A] = 52, // Caps Lock
            [0x1E] = 53, // A
            [0x1F] = 54, // S
            [0x20] = 55, // D
            [0x21] = 56, // F
            [0x22] = 57, // G
            [0x23] = 58, // H
            [0x24] = 59, // J
            [0x25] = 60, // K
            [0x26] = 61, // L
            [0x27] = 62, // ;
            [0x28] = 63, // '
            [0x1C] = 66, // Enter

            [0x2A] = 69, // Left Shift
            [0x2C] = 71, // Z
            [0x2D] = 72, // X
            [0x2E] = 73, // C
            [0x2F] = 74, // V
            [0x30] = 75, // B
            [0x31] = 76, // N
            [0x32] = 77, // M
            [0x33] = 78, // ,
            [0x34] = 79, // .
            [0x35] = 80, // /
            [0x36] = 83, // Right Shift

            [0x1D] = 86, // Left Ctrl
            [0x38] = 90, // Left Alt
        };

    private static readonly IReadOnlyDictionary<ushort, byte> ExtendedScanCodes =
        new Dictionary<ushort, byte>
        {
            [0x53] = 15,  // Delete
            [0x5B] = 88,  // Left Windows
            [0x38] = 94,  // Right Alt
            [0x1D] = 96,  // Right Ctrl
            [0x4B] = 97,  // Left Arrow
            [0x48] = 98,  // Up Arrow
            [0x4D] = 99,  // Right Arrow
            [0x50] = 100, // Down Arrow
        };

    public static bool TryMap(
        ushort makeCode,
        bool isExtended,
        out ReactiveKeyPulse primary,
        out ReactiveKeyPulse? secondary)
    {
        // Fn+F12 is the model's Print Screen chord. The MI_01 HID report emits
        // Fn separately; if Windows translates the second report to E0 37,
        // this maps only its F12 half and avoids pulsing Fn twice.
        if (isExtended && makeCode == 0x37)
        {
            primary = new ReactiveKeyPulse(13);
            secondary = null;
            return true;
        }

        IReadOnlyDictionary<ushort, byte> map = isExtended
            ? ExtendedScanCodes
            : StandardScanCodes;
        if (map.TryGetValue(makeCode, out byte slot))
        {
            primary = new ReactiveKeyPulse(slot);
            secondary = null;
            return true;
        }

        primary = default;
        secondary = null;
        return false;
    }
}

/// <summary>
/// Maps Windows scan-code make events to the independently observed
/// RZ09-0528 / PID 02C6 matrix. The table is intentionally separate from the
/// RZ09-0581 map: the exact LampArray geometry adds Insert, Page Up, and Page
/// Down, places Right Alt at slot 91, and leaves slots 16, 67, 84, 87, 94,
/// and 101 without virtual-key ownership. A physical Synapse oracle also
/// confirmed J at slot 59 for both Reactive and Ripple.
/// </summary>
public static class Rz09_0528ReactiveKeyMap
{
    private static readonly IReadOnlyDictionary<ushort, byte> StandardScanCodes =
        new Dictionary<ushort, byte>
        {
            [0x01] = 1,  // Escape

            [0x3B] = 2,  // F1
            [0x3C] = 3,  // F2
            [0x3D] = 4,  // F3
            [0x3E] = 5,  // F4
            [0x3F] = 6,  // F5
            [0x40] = 7,  // F6
            [0x41] = 8,  // F7
            [0x42] = 9,  // F8
            [0x43] = 10, // F9
            [0x44] = 11, // F10
            [0x57] = 12, // F11
            [0x58] = 13, // F12

            [0x29] = 18, // `
            [0x02] = 19, // 1
            [0x03] = 20, // 2
            [0x04] = 21, // 3
            [0x05] = 22, // 4
            [0x06] = 23, // 5
            [0x07] = 24, // 6
            [0x08] = 25, // 7
            [0x09] = 26, // 8
            [0x0A] = 27, // 9
            [0x0B] = 28, // 0
            [0x0C] = 29, // -
            [0x0D] = 30, // =
            [0x0E] = 32, // Backspace

            [0x0F] = 35, // Tab
            [0x10] = 36, // Q
            [0x11] = 37, // W
            [0x12] = 38, // E
            [0x13] = 39, // R
            [0x14] = 40, // T
            [0x15] = 41, // Y
            [0x16] = 42, // U
            [0x17] = 43, // I
            [0x18] = 44, // O
            [0x19] = 45, // P
            [0x1A] = 46, // [
            [0x1B] = 47, // ]
            [0x2B] = 49, // \

            [0x3A] = 52, // Caps Lock
            [0x1E] = 53, // A
            [0x1F] = 54, // S
            [0x20] = 55, // D
            [0x21] = 56, // F
            [0x22] = 57, // G
            [0x23] = 58, // H
            [0x24] = 59, // J
            [0x25] = 60, // K
            [0x26] = 61, // L
            [0x27] = 62, // ;
            [0x28] = 63, // '
            [0x1C] = 66, // Enter

            [0x2A] = 69, // Left Shift
            [0x2C] = 71, // Z
            [0x2D] = 72, // X
            [0x2E] = 73, // C
            [0x2F] = 74, // V
            [0x30] = 75, // B
            [0x31] = 76, // N
            [0x32] = 77, // M
            [0x33] = 78, // ,
            [0x34] = 79, // .
            [0x35] = 80, // /
            [0x36] = 83, // Right Shift

            [0x1D] = 86, // Left Ctrl
            [0x38] = 90, // Left Alt
        };

    private static readonly IReadOnlyDictionary<ushort, byte> ExtendedScanCodes =
        new Dictionary<ushort, byte>
        {
            [0x52] = 14,  // Insert
            [0x53] = 15,  // Delete
            [0x49] = 33,  // Page Up
            [0x51] = 50,  // Page Down
            [0x5B] = 88,  // Left Windows
            [0x38] = 91,  // Right Alt
            [0x1D] = 96,  // Right Ctrl
            [0x4B] = 97,  // Left Arrow
            [0x48] = 98,  // Up Arrow
            [0x4D] = 99,  // Right Arrow
            [0x50] = 100, // Down Arrow
        };

    public static bool TryMap(
        ushort makeCode,
        bool isExtended,
        out ReactiveKeyPulse primary,
        out ReactiveKeyPulse? secondary)
    {
        IReadOnlyDictionary<ushort, byte> map = isExtended
            ? ExtendedScanCodes
            : StandardScanCodes;
        if (map.TryGetValue(makeCode, out byte slot))
        {
            primary = new ReactiveKeyPulse(slot);
            secondary = null;
            return true;
        }

        primary = default;
        secondary = null;
        return false;
    }
}

/// <summary>
/// Reduces a RAWKEYBOARD packet to a slot pulse. Only key-make scan-code data
/// crosses this boundary; key-up, E1, and unsupported events are dropped.
/// </summary>
public static class ReactiveRawKeyboardInput
{
    public const ushort KeyBreakFlag = 0x0001;
    public const ushort ExtendedE0Flag = 0x0002;
    public const ushort ExtendedE1Flag = 0x0004;

    public static bool TryCreatePulses(
        RazerBladeInputDeviceKind deviceKind,
        ushort makeCode,
        ushort flags,
        out ReactiveKeyPulse primary,
        out ReactiveKeyPulse? secondary)
    {
        if (makeCode == 0
            || (flags & KeyBreakFlag) != 0
            || (flags & ExtendedE1Flag) != 0)
        {
            primary = default;
            secondary = null;
            return false;
        }

        bool isExtended = (flags & ExtendedE0Flag) != 0;
        return deviceKind switch
        {
            RazerBladeInputDeviceKind.Rz09_0581 =>
                Rz09_0581ReactiveKeyMap.TryMap(
                    makeCode,
                    isExtended,
                    out primary,
                    out secondary),
            RazerBladeInputDeviceKind.Rz09_0484 =>
                TryCreateBlade18Pulse(makeCode, isExtended, out primary, out secondary),
            RazerBladeInputDeviceKind.Rz09_0528 =>
                Rz09_0528ReactiveKeyMap.TryMap(
                    makeCode,
                    isExtended,
                    out primary,
                    out secondary),
            _ => ReturnUnsupported(out primary, out secondary),
        };
    }

    private static bool TryCreateBlade18Pulse(
        ushort makeCode, bool extended,
        out ReactiveKeyPulse primary, out ReactiveKeyPulse? secondary)
    {
        secondary = null;
        if (Blade18KeyboardLightingLayout.TryMapScanCode(makeCode, extended, out byte slot))
        {
            primary = new(slot);
            return true;
        }
        primary = default;
        return false;
    }

    private static bool ReturnUnsupported(
        out ReactiveKeyPulse primary,
        out ReactiveKeyPulse? secondary)
    {
        primary = default;
        secondary = null;
        return false;
    }
}

/// <summary>
/// Recognizes only the two performance shortcut report sequences captured from
/// the RZ09-0581 MI_01 collection. The parser emits a source identity and keeps
/// no key text, foreground-app, or user activity history.
/// </summary>
public static class Rz09_0581PerformanceShortcutRawHidInput
{
    private const byte FunctionReportId = 0x04;
    private const byte KeyboardReportId = 0x01;
    private const byte FunctionPressedValue = 0x0A;
    private const byte DedicatedPerformanceValue = 0xD3;
    private const byte PUsage = 0x13;
    private const ushort PScanCode = 0x19;

    public static bool TryCreateTrigger(
        ReadOnlySpan<byte> report,
        ref bool functionKeyPressed,
        ref bool dedicatedButtonPressed,
        ref bool functionPPressed,
        out PerformanceShortcutSource source)
    {
        source = default;
        if (report.Length < 3)
        {
            return false;
        }

        if (report[0] == FunctionReportId)
        {
            bool functionNow = report[1] == FunctionPressedValue;
            bool dedicatedNow = report[1] == DedicatedPerformanceValue
                || functionNow && report[2] == DedicatedPerformanceValue;
            bool trigger = dedicatedNow && !dedicatedButtonPressed;
            dedicatedButtonPressed = dedicatedNow;
            functionKeyPressed = functionNow;
            if (!functionNow)
            {
                functionPPressed = false;
            }

            if (trigger)
            {
                source = PerformanceShortcutSource.DedicatedButton;
                return true;
            }

            return false;
        }

        if (report[0] != KeyboardReportId)
        {
            return false;
        }

        bool functionPNow = functionKeyPressed
            && report[1] == 0
            && report[2] == PUsage;
        bool functionPTrigger = functionPNow && !functionPPressed;
        functionPPressed = functionPNow;
        if (functionPTrigger)
        {
            source = PerformanceShortcutSource.FunctionP;
            return true;
        }

        return false;
    }

    public static bool TryCreateFunctionPTrigger(
        ushort makeCode,
        ushort flags,
        bool functionKeyPressed,
        ref bool functionPPressed,
        out PerformanceShortcutSource source)
    {
        source = default;
        if (makeCode != PScanCode
            || (flags & ReactiveRawKeyboardInput.ExtendedE1Flag) != 0)
        {
            return false;
        }

        bool functionPNow = functionKeyPressed
            && (flags & ReactiveRawKeyboardInput.KeyBreakFlag) == 0;
        bool trigger = functionPNow && !functionPPressed;
        functionPPressed = functionPNow;
        if (!trigger)
        {
            return false;
        }

        source = PerformanceShortcutSource.FunctionP;
        return true;
    }
}

/// <summary>
/// Recognizes only the Fn+P sequence captured from PID 02C6. In particular,
/// 04/0A/D3 is M4 on this model and must never be treated as a dedicated
/// performance shortcut.
/// </summary>
public static class Rz09_0528PerformanceShortcutRawHidInput
{
    private const byte KeyboardReportId = 0x01;
    private const byte PUsage = 0x13;

    public static bool TryCreateTrigger(
        ReadOnlySpan<byte> report,
        bool functionKeyPressed,
        ref bool functionPPressed,
        out PerformanceShortcutSource source)
    {
        source = default;
        if (report.Length < 3)
        {
            return false;
        }
        if (report[0] != KeyboardReportId)
        {
            if (!functionKeyPressed)
            {
                functionPPressed = false;
            }
            return false;
        }

        bool functionPNow = functionKeyPressed
            && report[1] == 0
            && report[2] == PUsage;
        bool trigger = functionPNow && !functionPPressed;
        functionPPressed = functionPNow;
        if (!trigger)
        {
            return false;
        }

        source = PerformanceShortcutSource.FunctionP;
        return true;
    }

}

public enum RazerBladeInputDeviceKind
{
    Unsupported,
    Rz09_0581,
    Rz09_0528,
    Rz09_0484,
}

public static class RazerBladeReactiveInputDevice
{
    public static RazerBladeInputDeviceKind ClassifyPath(string? path)
    {
        // Product 672's SYSTEM report geometry does not inherit MI_01.
        if (!string.IsNullOrWhiteSpace(path) && HasIdentity(path, "VID_1532&PID_02A0"))
        {
            return RazerBladeInputDeviceKind.Rz09_0484;
        }
        if (string.IsNullOrWhiteSpace(path)
            || !path.Contains("&MI_01", StringComparison.OrdinalIgnoreCase))
        {
            return RazerBladeInputDeviceKind.Unsupported;
        }

        if (HasIdentity(path, "VID_1532&PID_02E0"))
        {
            return RazerBladeInputDeviceKind.Rz09_0581;
        }

        return HasIdentity(path, "VID_1532&PID_02C6")
            ? RazerBladeInputDeviceKind.Rz09_0528
            : RazerBladeInputDeviceKind.Unsupported;
    }

    public static RazerBladeInputDeviceKind ClassifyModel(string? modelNumber) =>
        modelNumber switch
        {
            RazerBladeModelCatalog.OpenBladeTargetModelNumber =>
                RazerBladeInputDeviceKind.Rz09_0581,
            RazerBladeModelCatalog.Rz090528ModelNumber =>
                RazerBladeInputDeviceKind.Rz09_0528,
            RazerBladeModelCatalog.Rz090484ModelNumber =>
                RazerBladeInputDeviceKind.Rz09_0484,
            _ => RazerBladeInputDeviceKind.Unsupported,
        };

    private static bool HasIdentity(string path, string identity)
    {
        int index = path.IndexOf(identity, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        int suffix = index + identity.Length;
        return suffix == path.Length || path[suffix] is '&' or '#' or '\\';
    }
}

public static class Rz09_0581ReactiveInputDevice
{
    public static bool IsTargetPath(string? path) =>
        RazerBladeReactiveInputDevice.ClassifyPath(path)
            == RazerBladeInputDeviceKind.Rz09_0581;
}

public static class Rz09_0528ReactiveInputDevice
{
    public static bool IsTargetPath(string? path) =>
        RazerBladeReactiveInputDevice.ClassifyPath(path)
            == RazerBladeInputDeviceKind.Rz09_0528;
}
