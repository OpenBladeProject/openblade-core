using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class Blade18ReactiveInputTests
{
    [Theory]
    [InlineData(0x10, 0, 34)]
    [InlineData(0x1E, 0, 50)]
    [InlineData(0x24, 0, 56)]
    [InlineData(0x39, 0, 86)]
    [InlineData(0x53, 2, 15)]
    public void Blade18Makes_UseRecoveredDeviceSlots(ushort scanCode, ushort flags, byte slot)
    {
        Assert.True(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0484, scanCode, flags,
            out ReactiveKeyPulse pulse, out ReactiveKeyPulse? secondary));
        Assert.Equal(slot, pulse.MatrixSlot);
        Assert.Null(secondary);
    }

    [Theory]
    [InlineData(0x10, 1)]
    [InlineData(0x10, 4)]
    [InlineData(0x1C, 0)]
    [InlineData(0x2B, 0)]
    [InlineData(0x38, 0)]
    [InlineData(0x38, 2)]
    public void BreaksAndUnprovenRegionalInputs_DoNotCreatePulses(ushort scanCode, ushort flags)
    {
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0484, scanCode, flags, out _, out _));
    }

    [Theory]
    [InlineData(1, 0x1c, 0, 63)]
    [InlineData(3, 0x1c, 0, 46)]
    [InlineData(12, 0x1c, 0, 46)]
    [InlineData(16, 0x1c, 0, 63)]
    [InlineData(1, 0x38, 0, 85)]
    [InlineData(12, 0x38, 0, 84)]
    [InlineData(12, 0x38, 2, 90)]
    [InlineData(3, 0x56, 0, 66)]
    [InlineData(12, 0x7b, 0, 85)]
    public void PhysicalRegionSelectsExactBinding(byte layout, ushort scanCode, ushort flags, byte expected)
    {
        Assert.True(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0484, layout, scanCode, flags,
            out ReactiveKeyPulse pulse, out ReactiveKeyPulse? secondary));
        Assert.Equal(expected, pulse.MatrixSlot);
        Assert.Null(secondary);
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0484, layout, scanCode, (ushort)(flags | 1), out _, out _));
    }

    [Fact]
    public void Blade18Input_DoesNotReplaceOtherModelsMaps()
    {
        Assert.True(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0581, 0x10, 0,
            out ReactiveKeyPulse previous, out _));
        Assert.Equal(36, previous.MatrixSlot);
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Unsupported, 0x10, 0, out _, out _));
    }
}
