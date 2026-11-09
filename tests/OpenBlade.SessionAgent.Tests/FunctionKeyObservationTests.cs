using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class FunctionKeyObservationTests
{
    [Theory]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0581)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0528)]
    public void StartupDoesNotAssumeHeldFnIsReleased(RazerBladeInputDeviceKind deviceKind)
    {
        var state = new FunctionKeyObservation();
        Assert.False(state.Known);
        Assert.Null(state.Read());
        state.Observe(deviceKind, [0x01, 0, 0]);
        state.Observe(deviceKind, [0x04, 0x0A]);
        Assert.False(state.Known);
        state.Observe(deviceKind, [0x04, 0x0A, 0]);
        Assert.True(state.Known);
        Assert.True(state.Pressed);
        Assert.True(state.Read());
        state.Observe(deviceKind, [0x01, 0, 0]);
        Assert.True(state.Pressed);
        state.Observe(deviceKind, [0x04, 0, 0]);
        Assert.True(state.Known);
        Assert.False(state.Pressed);
        Assert.False(state.Read());
        state.Invalidate();
        Assert.False(state.Known);
    }

    [Fact]
    public void MalformedFnReleaseInvalidatesConfirmedHold()
    {
        var state = new FunctionKeyObservation();
        state.Observe(RazerBladeInputDeviceKind.Rz09_0581, [0x04, 0x0A, 0]);
        Assert.True(state.Read());
        state.Observe(RazerBladeInputDeviceKind.Rz09_0581, [0x04, 0]);
        Assert.Null(state.Read());
    }

    [Fact]
    public void AnotherModelCannotEstablishFunctionKeyState()
    {
        var state = new FunctionKeyObservation();
        state.Observe(RazerBladeInputDeviceKind.Rz09_0484, [0x04, 0x0A, 0]);
        Assert.False(state.Known);
    }
}
