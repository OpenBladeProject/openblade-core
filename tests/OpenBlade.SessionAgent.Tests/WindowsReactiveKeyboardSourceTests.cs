using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsReactiveKeyboardSourceTests
{
    [Theory]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0528, SpecialKeySlot.M4, false, true)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0528, SpecialKeySlot.M3, false, false)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0581, SpecialKeySlot.M4, false, false)]
    [InlineData(RazerBladeInputDeviceKind.Unsupported, SpecialKeySlot.M4, false, false)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0528, SpecialKeySlot.M1, true, true)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0581, SpecialKeySlot.M5, true, true)]
    public void ShouldOverrideSpecialKey_PreservesExactM4DefaultWithoutChangingOlderPath(
        RazerBladeInputDeviceKind deviceKind,
        SpecialKeySlot slot,
        bool configuredOverride,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsReactiveKeyboardSource.ShouldOverrideSpecialKey(
                deviceKind,
                slot,
                configuredOverride));
    }
}
