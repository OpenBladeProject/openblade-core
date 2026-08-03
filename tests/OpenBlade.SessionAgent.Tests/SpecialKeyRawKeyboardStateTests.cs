using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SpecialKeyRawKeyboardStateTests
{
    [Fact]
    public void CapturedM1Sequence_TriggersEveryCompletePress()
    {
        Span<bool> pressed = stackalloc bool[5];

        for (int press = 0; press < 3; press++)
        {
            Assert.True(TryM1(
                keyDown: true,
                functionKeyPressed: true,
                pressed,
                out SpecialKeySlot slot));
            Assert.Equal(SpecialKeySlot.M1, slot);
            Assert.False(TryM1(
                keyDown: false,
                functionKeyPressed: true,
                pressed,
                out _));
        }
    }

    [Fact]
    public void KeyRepeat_DoesNotRetriggerUntilKeyUp()
    {
        Span<bool> pressed = stackalloc bool[5];

        Assert.True(TryM1(true, true, pressed, out _));
        Assert.False(TryM1(true, true, pressed, out _));
        Assert.False(TryM1(false, true, pressed, out _));
        Assert.True(TryM1(true, true, pressed, out _));
    }

    [Theory]
    [InlineData(false, RazerBladeInputDeviceKind.Rz09_0581)]
    [InlineData(true, RazerBladeInputDeviceKind.Rz09_0528)]
    [InlineData(true, RazerBladeInputDeviceKind.Unsupported)]
    public void IneligiblePageUp_DoesNotTrigger(
        bool functionKeyPressed,
        RazerBladeInputDeviceKind deviceKind)
    {
        Span<bool> pressed = stackalloc bool[5];

        Assert.False(SpecialKeyRawKeyboardState.TryCreateTrigger(
            deviceKind,
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
            keyDown: true,
            functionKeyPressed,
            pressed,
            out _));
    }

    [Fact]
    public void M2_MapsPageDownAndReleaseClearsPressedState()
    {
        Span<bool> pressed = stackalloc bool[5];

        Assert.True(SpecialKeyRawKeyboardState.TryCreateTrigger(
            RazerBladeInputDeviceKind.Rz09_0581,
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageDown,
            keyDown: true,
            functionKeyPressed: true,
            pressed,
            out SpecialKeySlot slot));
        Assert.Equal(SpecialKeySlot.M2, slot);
        Assert.False(SpecialKeyRawKeyboardState.TryCreateTrigger(
            RazerBladeInputDeviceKind.Rz09_0581,
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageDown,
            keyDown: false,
            functionKeyPressed: false,
            pressed,
            out _));
        Assert.True(SpecialKeyRawKeyboardState.TryCreateTrigger(
            RazerBladeInputDeviceKind.Rz09_0581,
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageDown,
            keyDown: true,
            functionKeyPressed: true,
            pressed,
            out _));
    }

    private static bool TryM1(
        bool keyDown,
        bool functionKeyPressed,
        Span<bool> pressed,
        out SpecialKeySlot slot) =>
        SpecialKeyRawKeyboardState.TryCreateTrigger(
            RazerBladeInputDeviceKind.Rz09_0581,
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
            keyDown,
            functionKeyPressed,
            pressed,
            out slot);
}
