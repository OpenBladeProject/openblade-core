using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class FunctionKeyObservationTests
{
    [Theory]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0581)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0528)]
    public void StartupAssumesFnReleasedUntilItsFirstReport(RazerBladeInputDeviceKind deviceKind)
    {
        var state = new FunctionKeyObservation();
        Assert.True(state.Known);
        Assert.False(state.Pressed);
        Assert.False(state.Read());
        state.Observe(deviceKind, [0x01, 0, 0]);
        Assert.False(state.Read());
        state.Observe(deviceKind, [0x04, 0x0A]);
        Assert.False(state.Known);
        Assert.Null(state.Read());
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

    // The first service status and every Raw Input device arrival reset the
    // source, so a reset that restored unknown would bring the prompt back.
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceResetReturnsBlade16FnToReleasedAndBlade18ToUnknown(bool? fnHeld)
    {
        var state = new FunctionKeyObservation();
        if (fnHeld is null) { state.Invalidate(); }
        else { state.Observe(RazerBladeInputDeviceKind.Rz09_0581, [0x04, fnHeld.Value ? (byte)0x0A : (byte)0, 0]); }
        var blade18 = new Rz090484FnInputState();
        byte[] report = new byte[16];
        report[0] = 4;
        report[15] = 0x0A;
        blade18.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 1, report, true);
        Assert.True(blade18.Read());

        WindowsReactiveKeyboardSource.ResetFunctionKeyKnowledge(state, blade18);

        Assert.False(state.Read());
        Assert.Null(blade18.Read());
    }

    [Fact]
    public void AnotherModelCannotEstablishFunctionKeyState()
    {
        var state = new FunctionKeyObservation();
        state.Observe(RazerBladeInputDeviceKind.Rz09_0484, [0x04, 0x0A, 0]);
        Assert.False(state.Read());
        state.Invalidate();
        state.Observe(RazerBladeInputDeviceKind.Rz09_0484, [0x04, 0, 0]);
        Assert.False(state.Known);
    }
}
