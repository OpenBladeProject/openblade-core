using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class FanStopPowerTransitionPolicyTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn, PowerSource.PluggedIn, (int)FanStopPowerTransition.Continue)]
    [InlineData(PowerSource.UsbC, PowerSource.UsbC, (int)FanStopPowerTransition.Continue)]
    [InlineData(PowerSource.Battery, PowerSource.Battery, (int)FanStopPowerTransition.Continue)]
    [InlineData(PowerSource.PluggedIn, PowerSource.UsbC, (int)FanStopPowerTransition.VerifyOwnedStop)]
    [InlineData(PowerSource.PluggedIn, PowerSource.Battery, (int)FanStopPowerTransition.VerifyOwnedStop)]
    [InlineData(PowerSource.UsbC, PowerSource.Battery, (int)FanStopPowerTransition.VerifyOwnedStop)]
    [InlineData(PowerSource.Battery, PowerSource.UsbC, (int)FanStopPowerTransition.Automatic)]
    [InlineData(PowerSource.Battery, PowerSource.PluggedIn, (int)FanStopPowerTransition.Automatic)]
    [InlineData(PowerSource.UsbC, PowerSource.PluggedIn, (int)FanStopPowerTransition.Automatic)]
    [InlineData(PowerSource.ExternalUnknown, PowerSource.Battery, (int)FanStopPowerTransition.Automatic)]
    [InlineData(PowerSource.UsbC, PowerSource.ExternalUnknown, (int)FanStopPowerTransition.Automatic)]
    [InlineData((PowerSource)99, PowerSource.UsbC, (int)FanStopPowerTransition.Automatic)]
    public void OnlyLowerPowerCanRetainOwnedStopAfterReadback(PowerSource previous, PowerSource current,
        int expected)
    {
        Assert.Equal((FanStopPowerTransition)expected, FanStopPowerTransitionPolicy.Evaluate(previous, current));
    }
}
