using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class FanControlPowerPolicyTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn, FanMode.ManualCurve, true)]
    [InlineData(PowerSource.UsbC, FanMode.Automatic, true)]
    [InlineData(PowerSource.UsbC, FanMode.Fixed, true)]
    [InlineData(PowerSource.UsbC, FanMode.ManualCurve, true)]
    [InlineData(PowerSource.Battery, FanMode.Automatic, true)]
    [InlineData(PowerSource.Battery, FanMode.Fixed, true)]
    [InlineData(PowerSource.Battery, FanMode.Maximum, true)]
    [InlineData(PowerSource.Battery, FanMode.ManualCurve, false)]
    [InlineData(PowerSource.ExternalUnknown, FanMode.Automatic, false)]
    [InlineData((PowerSource)999, FanMode.Fixed, false)]
    [InlineData(PowerSource.UsbC, (FanMode)999, false)]
    public void AvailableModesRequireConfirmedCompatiblePower(PowerSource source, FanMode mode, bool expected) =>
        Assert.Equal(expected, FanControlPowerPolicy.IsAvailable(source, mode));
}
