using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SpecialKeyKeyboardSuppressionStateTests
{
    [Theory]
    [InlineData(
        SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
        SpecialKeySlot.M1)]
    [InlineData(
        SpecialKeyKeyboardSuppressionState.VirtualKeyPageDown,
        SpecialKeySlot.M2)]
    public void AdmittedOverride_SuppressesDownRepeatAndUp(
        uint virtualKey,
        SpecialKeySlot expectedSlot)
    {
        var state = new SpecialKeyKeyboardSuppressionState();
        var observed = new List<SpecialKeySlot>();

        Assert.True(state.ShouldSuppress(
            virtualKey,
            keyDown: true,
            functionKeyPressed: true,
            RazerBladeInputDeviceKind.Rz09_0581,
            slot =>
            {
                observed.Add(slot);
                return true;
            },
            _ => true,
            out SpecialKeySlot? firstTrigger));
        Assert.Equal(expectedSlot, firstTrigger);
        Assert.True(state.ShouldSuppress(
            virtualKey,
            keyDown: true,
            functionKeyPressed: false,
            RazerBladeInputDeviceKind.Unsupported,
            _ => false,
            _ => false,
            out SpecialKeySlot? repeatTrigger));
        Assert.Null(repeatTrigger);
        Assert.True(state.ShouldSuppress(
            virtualKey,
            keyDown: false,
            functionKeyPressed: false,
            RazerBladeInputDeviceKind.Unsupported,
            _ => false,
            _ => false,
            out SpecialKeySlot? releaseTrigger));
        Assert.Null(releaseTrigger);

        Assert.Equal([expectedSlot], observed);
        Assert.False(state.ShouldSuppress(
            virtualKey,
            keyDown: false,
            functionKeyPressed: true,
            RazerBladeInputDeviceKind.Rz09_0581,
            _ => true,
            _ => true));
    }

    [Theory]
    [InlineData(false, true, RazerBladeInputDeviceKind.Rz09_0581)]
    [InlineData(true, false, RazerBladeInputDeviceKind.Rz09_0581)]
    [InlineData(true, true, RazerBladeInputDeviceKind.Rz09_0528)]
    [InlineData(true, true, RazerBladeInputDeviceKind.Unsupported)]
    public void IneligibleFirstDown_PassesTheWholePressThrough(
        bool admitted,
        bool overridden,
        RazerBladeInputDeviceKind deviceKind)
    {
        var state = new SpecialKeyKeyboardSuppressionState();

        Assert.False(state.ShouldSuppress(
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
            keyDown: true,
            functionKeyPressed: true,
            deviceKind,
            _ => admitted,
            _ => overridden));
        Assert.False(state.ShouldSuppress(
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
            keyDown: true,
            functionKeyPressed: true,
            RazerBladeInputDeviceKind.Rz09_0581,
            _ => true,
            _ => true));
        Assert.False(state.ShouldSuppress(
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
            keyDown: false,
            functionKeyPressed: true,
            RazerBladeInputDeviceKind.Rz09_0581,
            _ => true,
            _ => true));
    }

    [Fact]
    public void OrdinaryPageKeyWithoutFn_PassesTheWholePressThrough()
    {
        var state = new SpecialKeyKeyboardSuppressionState();

        Assert.False(state.ShouldSuppress(
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
            keyDown: true,
            functionKeyPressed: false,
            RazerBladeInputDeviceKind.Rz09_0581,
            _ => true,
            _ => true));
        Assert.False(state.ShouldSuppress(
            SpecialKeyKeyboardSuppressionState.VirtualKeyPageUp,
            keyDown: false,
            functionKeyPressed: true,
            RazerBladeInputDeviceKind.Rz09_0581,
            _ => true,
            _ => true));
    }

    [Fact]
    public void UnrelatedKey_IsNeverSuppressedOrQueried()
    {
        var state = new SpecialKeyKeyboardSuppressionState();

        Assert.False(state.ShouldSuppress(
            virtualKey: 0x23,
            keyDown: true,
            functionKeyPressed: true,
            RazerBladeInputDeviceKind.Rz09_0581,
            _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException()));
    }
}
