using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class ReactiveKeyboardInputTests
{
    [Theory]
    [InlineData(0x01, 1)]  // Escape
    [InlineData(0x3B, 2)]  // F1
    [InlineData(0x44, 11)] // F10
    [InlineData(0x57, 12)] // F11
    [InlineData(0x58, 13)] // F12
    [InlineData(0x29, 18)] // `
    [InlineData(0x02, 19)] // 1
    [InlineData(0x0B, 28)] // 0
    [InlineData(0x0C, 29)] // -
    [InlineData(0x0D, 30)] // =
    [InlineData(0x0E, 32)] // Backspace
    [InlineData(0x0F, 35)] // Tab
    [InlineData(0x10, 36)] // Q
    [InlineData(0x19, 45)] // P
    [InlineData(0x1A, 46)] // [
    [InlineData(0x1B, 47)] // ]
    [InlineData(0x2B, 49)] // \
    [InlineData(0x3A, 52)] // Caps Lock
    [InlineData(0x1E, 53)] // A
    [InlineData(0x26, 61)] // L
    [InlineData(0x27, 62)] // ;
    [InlineData(0x28, 63)] // '
    [InlineData(0x1C, 66)] // Enter
    [InlineData(0x2A, 69)] // Left Shift
    [InlineData(0x2C, 71)] // Z
    [InlineData(0x32, 77)] // M
    [InlineData(0x33, 78)] // ,
    [InlineData(0x34, 79)] // .
    [InlineData(0x35, 80)] // /
    [InlineData(0x36, 83)] // Right Shift
    [InlineData(0x1D, 86)] // Left Ctrl
    [InlineData(0x38, 90)] // Left Alt
    public void Map_UsesTheRz09_0581StandardLayout(
        ushort makeCode,
        byte expectedSlot)
    {
        Assert.True(Rz09_0581ReactiveKeyMap.TryMap(
            makeCode,
            isExtended: false,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary));
        Assert.Equal(expectedSlot, primary.MatrixSlot);
        Assert.Null(secondary);
    }

    [Theory]
    [InlineData(0x53, 15)]  // Delete
    [InlineData(0x5B, 88)]  // Left Windows
    [InlineData(0x38, 94)]  // Right Alt
    [InlineData(0x1D, 96)]  // Right Ctrl
    [InlineData(0x4B, 97)]  // Left Arrow
    [InlineData(0x48, 98)]  // Up Arrow
    [InlineData(0x4D, 99)]  // Right Arrow
    [InlineData(0x50, 100)] // Down Arrow
    public void Map_UsesCaptureConfirmedExtendedKeys(
        ushort makeCode,
        byte expectedSlot)
    {
        Assert.True(Rz09_0581ReactiveKeyMap.TryMap(
            makeCode,
            isExtended: true,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary));
        Assert.Equal(expectedSlot, primary.MatrixSlot);
        Assert.Null(secondary);
    }

    [Fact]
    public void Map_PrintScreenTranslationEmitsOnlyF12BecauseFnHasItsOwnHidReport()
    {
        Assert.True(Rz09_0581ReactiveKeyMap.TryMap(
            0x37,
            isExtended: true,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary));
        Assert.Equal(13, primary.MatrixSlot);
        Assert.Null(secondary);
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x39, false)] // Space bar has no visible lighting on this model.
    [InlineData(0x54, false)]
    [InlineData(0x01, true)]
    [InlineData(0x5C, true)]
    public void Map_IgnoresKeysWithoutCaptureSupportedSlots(
        ushort makeCode,
        bool isExtended)
    {
        Assert.False(Rz09_0581ReactiveKeyMap.TryMap(
            makeCode,
            isExtended,
            out _,
            out _));
    }

    [Fact]
    public void RawInput_EmitsOnlySupportedKeyMakeEvents()
    {
        Assert.True(ReactiveRawKeyboardInput.TryCreatePulses(
            0x10,
            flags: 0,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary));
        Assert.Equal(36, primary.MatrixSlot);
        Assert.Null(secondary);

        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            0x10,
            ReactiveRawKeyboardInput.KeyBreakFlag,
            out _,
            out _));
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            0x10,
            ReactiveRawKeyboardInput.ExtendedE0Flag,
            out _,
            out _));
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            0x10,
            ReactiveRawKeyboardInput.ExtendedE1Flag,
            out _,
            out _));
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            0,
            flags: 0,
            out _,
            out _));
    }

    [Theory]
    [InlineData(0x01, 1)]  // Escape
    [InlineData(0x58, 13)] // F12
    [InlineData(0x29, 18)] // `
    [InlineData(0x0E, 32)] // Backspace
    [InlineData(0x0F, 35)] // Tab
    [InlineData(0x12, 38)] // E
    [InlineData(0x18, 44)] // O
    [InlineData(0x20, 55)] // D
    [InlineData(0x24, 59)] // J, physical Reactive/Ripple oracle
    [InlineData(0x1C, 66)] // Enter
    [InlineData(0x31, 76)] // N
    [InlineData(0x38, 90)] // Left Alt
    public void Rz090528Map_UsesExactPid02C6StandardSlots(
        ushort makeCode,
        byte expectedSlot)
    {
        Assert.True(Rz09_0528ReactiveKeyMap.TryMap(
            makeCode,
            isExtended: false,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary));
        Assert.Equal(expectedSlot, primary.MatrixSlot);
        Assert.Null(secondary);
    }

    [Theory]
    [InlineData(0x52, 14)]  // Insert
    [InlineData(0x53, 15)]  // Delete
    [InlineData(0x49, 33)]  // Page Up
    [InlineData(0x51, 50)]  // Page Down
    [InlineData(0x5B, 88)]  // Left Windows
    [InlineData(0x38, 91)]  // Right Alt; differs from RZ09-0581
    [InlineData(0x1D, 96)]  // Right Ctrl
    [InlineData(0x4B, 97)]  // Left Arrow
    [InlineData(0x48, 98)]  // Up Arrow
    [InlineData(0x4D, 99)]  // Right Arrow
    [InlineData(0x50, 100)] // Down Arrow
    public void Rz090528Map_UsesExactPid02C6ExtendedSlots(
        ushort makeCode,
        byte expectedSlot)
    {
        Assert.True(Rz09_0528ReactiveKeyMap.TryMap(
            makeCode,
            isExtended: true,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary));
        Assert.Equal(expectedSlot, primary.MatrixSlot);
        Assert.Null(secondary);
    }

    [Theory]
    [InlineData(0x37, true)]  // Print Screen has no PID 02C6 virtual-key lamp.
    [InlineData(0x39, false)] // Space bar has no PID 02C6 lamp.
    [InlineData(0x5C, true)]  // Right Windows was not exposed.
    public void Rz090528Map_DoesNotBorrowUnmappedRz090581Keys(
        ushort makeCode,
        bool isExtended)
    {
        Assert.False(Rz09_0528ReactiveKeyMap.TryMap(
            makeCode,
            isExtended,
            out _,
            out _));
    }

    [Fact]
    public void RawInput_RoutesThroughTheExactModelMap()
    {
        Assert.True(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0528,
            0x38,
            ReactiveRawKeyboardInput.ExtendedE0Flag,
            out ReactiveKeyPulse rz090528,
            out _));
        Assert.Equal(91, rz090528.MatrixSlot);

        Assert.True(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0581,
            0x38,
            ReactiveRawKeyboardInput.ExtendedE0Flag,
            out ReactiveKeyPulse rz090581,
            out _));
        Assert.Equal(94, rz090581.MatrixSlot);

        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Unsupported,
            0x24,
            flags: 0,
            out _,
            out _));
    }

    [Fact]
    public void Pulse_RejectsSlotsOutsideCapturedMatrix()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReactiveKeyPulse(102));
    }

    [Fact]
    public void RawHid_DedicatedPerformanceButtonEmitsOncePerPress()
    {
        bool functionPressed = false;
        bool dedicatedPressed = false;
        bool functionPPressed = false;

        Assert.True(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0xD3, 0x00, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out PerformanceShortcutSource source));
        Assert.Equal(PerformanceShortcutSource.DedicatedButton, source);
        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0xD3, 0x00, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out _));
        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0x00, 0x00, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out _));
        Assert.True(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0xD3, 0x00, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out source));
        Assert.Equal(PerformanceShortcutSource.DedicatedButton, source);
    }

    [Fact]
    public void RawHid_FnPRequiresCapturedFnThenPSequence()
    {
        bool functionPressed = false;
        bool dedicatedPressed = false;
        bool functionPPressed = false;

        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out _));
        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0x0A, 0x00, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out _));
        Assert.True(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out PerformanceShortcutSource source));
        Assert.Equal(PerformanceShortcutSource.FunctionP, source);
        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out _));
        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x00, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out _));
        Assert.True(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out source));
        Assert.Equal(PerformanceShortcutSource.FunctionP, source);
    }

    [Fact]
    public void RawKeyboard_FnPUsesTheWindowsPScanCodeObservedOnTargetHardware()
    {
        bool functionPPressed = false;

        Assert.True(Rz09_0581PerformanceShortcutRawHidInput.TryCreateFunctionPTrigger(
            0x19,
            0,
            functionKeyPressed: true,
            ref functionPPressed,
            out PerformanceShortcutSource source));
        Assert.Equal(PerformanceShortcutSource.FunctionP, source);
        Assert.True(functionPPressed);

        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateFunctionPTrigger(
            0x19,
            0,
            functionKeyPressed: true,
            ref functionPPressed,
            out _));
        Assert.False(Rz09_0581PerformanceShortcutRawHidInput.TryCreateFunctionPTrigger(
            0x19,
            ReactiveRawKeyboardInput.KeyBreakFlag,
            functionKeyPressed: true,
            ref functionPPressed,
            out _));
        Assert.False(functionPPressed);
    }

    [Fact]
    public void RawHid_DedicatedButtonWhileFnHeldMatchesCapturedCombinedReport()
    {
        bool functionPressed = true;
        bool dedicatedPressed = false;
        bool functionPPressed = false;

        Assert.True(Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0x0A, 0xD3, 0x00],
            ref functionPressed,
            ref dedicatedPressed,
            ref functionPPressed,
            out PerformanceShortcutSource source));
        Assert.Equal(PerformanceShortcutSource.DedicatedButton, source);
        Assert.True(functionPressed);
    }

    [Fact]
    public void Rz090528RawHid_FnPTriggersWithoutAdmittingD3PerformanceButton()
    {
        bool functionPPressed = false;

        Assert.False(Rz09_0528PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0x0A, 0xD3, 0x00],
            functionKeyPressed: true,
            ref functionPPressed,
            out _));
        Assert.True(Rz09_0528PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            functionKeyPressed: true,
            ref functionPPressed,
            out PerformanceShortcutSource source));
        Assert.Equal(PerformanceShortcutSource.FunctionP, source);
        Assert.False(Rz09_0528PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            functionKeyPressed: true,
            ref functionPPressed,
            out _));
        Assert.False(Rz09_0528PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x04, 0x00, 0x00, 0x00],
            functionKeyPressed: false,
            ref functionPPressed,
            out _));
        Assert.True(Rz09_0528PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            functionKeyPressed: true,
            ref functionPPressed,
            out source));
        Assert.False(Rz09_0528PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x00, 0x00],
            functionKeyPressed: true,
            ref functionPPressed,
            out _));
        Assert.True(Rz09_0528PerformanceShortcutRawHidInput.TryCreateTrigger(
            [0x01, 0x00, 0x13, 0x00],
            functionKeyPressed: true,
            ref functionPPressed,
            out source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\\\\?\\HID#VID_1532&PID_02E0&MI_00#keyboard")]
    [InlineData("\\\\?\\HID#VID_1234&PID_5678&MI_01#keyboard")]
    public void InputDevice_RejectsUnrelatedPaths(string? path)
    {
        Assert.False(Rz09_0581ReactiveInputDevice.IsTargetPath(path));
    }

    [Fact]
    public void InputDevice_AcceptsOnlyTheBladeMi01Collection()
    {
        Assert.True(Rz09_0581ReactiveInputDevice.IsTargetPath(
            "\\\\?\\HID#VID_1532&PID_02E0&MI_01&COL04#function"));
    }

    [Theory]
    [InlineData(
        "\\\\?\\HID#VID_1532&PID_02E0&MI_01&COL04#function",
        RazerBladeInputDeviceKind.Rz09_0581)]
    [InlineData(
        "\\\\?\\HID#VID_1532&PID_02C6&MI_01&COL04#function",
        RazerBladeInputDeviceKind.Rz09_0528)]
    [InlineData(
        "\\\\?\\HID#VID_1532&PID_02C6&MI_00&COL01#keyboard",
        RazerBladeInputDeviceKind.Unsupported)]
    [InlineData(
        "\\\\?\\HID#VID_1532&PID_02E0&MI_02&COL01#mouse",
        RazerBladeInputDeviceKind.Unsupported)]
    [InlineData(
        "\\\\?\\HID#VID_1532&PID_02C60&MI_01&COL04#unrelated",
        RazerBladeInputDeviceKind.Unsupported)]
    public void InputDevice_ClassificationPreservesExactPidAndCollection(
        string path,
        RazerBladeInputDeviceKind expected)
    {
        Assert.Equal(expected, RazerBladeReactiveInputDevice.ClassifyPath(path));
    }

    [Theory]
    [InlineData("RZ09-0581", RazerBladeInputDeviceKind.Rz09_0581)]
    [InlineData("RZ09-0528", RazerBladeInputDeviceKind.Rz09_0528)]
    [InlineData("RZ09-9999", RazerBladeInputDeviceKind.Unsupported)]
    [InlineData(null, RazerBladeInputDeviceKind.Unsupported)]
    public void InputDevice_ModelClassificationFailsClosed(
        string? model,
        RazerBladeInputDeviceKind expected)
    {
        Assert.Equal(expected, RazerBladeReactiveInputDevice.ClassifyModel(model));
    }
}
