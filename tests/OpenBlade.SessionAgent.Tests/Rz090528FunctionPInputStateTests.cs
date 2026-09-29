using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class Rz090528FunctionPInputStateTests
{
    private const RazerBladeInputDeviceKind Target = RazerBladeInputDeviceKind.Rz09_0528;

    [Fact]
    public void CapturedSixSequenceTranscriptTriggersOnlyFunctionP()
    {
        var state = new Rz090528FunctionPInputState();
        (ushort Scan, ushort Flags, ushort Key)[] keys =
            [(0x49, 2, 0x21), (0x51, 2, 0x22), (0x19, 0, 0x50)];
        foreach ((ushort scan, ushort flags, ushort key) in keys)
        {
            state.ObserveHid(Target, [0x04, 0x0A, 0], true);
            Assert.Equal(key == 0x50, state.TryCreateTrigger(Target, scan, flags, key, true));
            Assert.False(state.TryCreateTrigger(Target, scan, (ushort)(flags | 1), key, true));
            state.ObserveHid(Target, [0x04, 0, 0], true);
        }
        foreach ((ushort scan, ushort flags, ushort key) in keys)
        {
            Assert.False(state.TryCreateTrigger(Target, scan, flags, key, true));
            Assert.False(state.TryCreateTrigger(Target, scan, (ushort)(flags | 1), key, true));
        }
    }

    [Fact]
    public void HeldPDoesNotRetriggerAcrossRepeatOrFunctionRelease()
    {
        var state = new Rz090528FunctionPInputState();
        state.ObserveHid(Target, [0x04, 0x0A, 0], true);
        Assert.True(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
        Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
        state.ObserveHid(Target, [0x04, 0, 0], true);
        state.ObserveHid(Target, [0x04, 0x0A, 0], true);
        Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
        Assert.False(state.TryCreateTrigger(Target, 0x19, 1, 0x50, true));
        Assert.True(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
    }

    [Fact]
    public void PPressedBeforeFunctionDoesNotBecomeShortcutOnRepeat()
    {
        var state = new Rz090528FunctionPInputState();
        Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
        state.ObserveHid(Target, [0x04, 0x0A, 0], true);
        Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReleaseRemovalOrLostAdmissionRequiresFreshFunctionMake(int boundary)
    {
        var state = new Rz090528FunctionPInputState();
        state.ObserveHid(Target, [0x04, 0x0A, 0], true);
        switch (boundary)
        {
            case 0: state.ObserveHid(Target, [0x04, 0, 0], true); break;
            case 1: state.Reset(); break;
            case 2: state.ObserveHid(Target, [0x04, 0x0A, 0], false); break;
            case 3: Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, false)); break;
        }
        Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
    }

    [Theory]
    [InlineData(RazerBladeInputDeviceKind.Unsupported, 0x19, 0, 0x50)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0581, 0x19, 0, 0x50)]
    [InlineData(Target, 0x19, 2, 0x50)]
    [InlineData(Target, 0x19, 4, 0x50)]
    [InlineData(Target, 0x20, 0, 0x50)]
    [InlineData(Target, 0x19, 0, 0x51)]
    public void RejectsUncapturedKeyboardIdentity(
        RazerBladeInputDeviceKind kind, ushort scan, ushort flags, ushort key)
    {
        var state = new Rz090528FunctionPInputState();
        state.ObserveHid(Target, [0x04, 0x0A, 0], true);
        Assert.False(state.TryCreateTrigger(kind, scan, flags, key, true));
        Assert.True(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
    }

    [Fact]
    public void LifecycleResetWithoutInterveningInputClearsFunctionBeforeNextKey()
    {
        var state = new Rz090528FunctionPInputState();
        state.ObserveHid(Target, [0x04, 0x0A, 0], true);
        state.RequestReset();
        // Admission recovered before any packet arrived during the boundary.
        Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
        Assert.False(state.TryCreateTrigger(Target, 0x19, 1, 0x50, true));
        state.ObserveHid(Target, [0x04, 0x0A, 0], true);
        Assert.True(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
    }

    [Fact]
    public void OtherDeviceFunctionAndKeyboardHidCannotArmShortcut()
    {
        var state = new Rz090528FunctionPInputState();
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0581, [0x04, 0x0A, 0], true);
        state.ObserveHid(Target, [0x01, 0, 0x13], true);
        Assert.False(state.TryCreateTrigger(Target, 0x19, 0, 0x50, true));
    }
}
