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
    [InlineData(0x1D, 2)]
    public void BreaksAndUnprovenRegionalInputs_DoNotCreatePulses(ushort scanCode, ushort flags)
    {
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(
            RazerBladeInputDeviceKind.Rz09_0484, scanCode, flags, out _, out _));
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
