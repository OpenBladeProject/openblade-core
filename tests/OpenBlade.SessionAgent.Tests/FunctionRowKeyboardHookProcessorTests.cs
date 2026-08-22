using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class FunctionRowKeyboardHookProcessorTests
{
    [Fact]
    public void PublishesReleaseWhenRecoveredKeyUpMustPassThrough()
    {
        var state = new RazerBladeFunctionRowInputState();
        var published = new List<FunctionRowInputEvent>();

        Assert.True(Process(state, keyDown: true, published.Add));
        Assert.True(state.ReconcilePassedThroughEdge(
            0x43,
            keyDown: true,
            out FunctionRowInputEvent? recoveryEvent));
        Assert.Null(recoveryEvent);

        Assert.False(Process(state, keyDown: false, published.Add));
        Assert.Equal(
            [
                new FunctionRowInputEvent(
                    0x43,
                    FunctionRowAction.DisplayBrightnessUp,
                    FunctionRowInputPhase.Down),
                new FunctionRowInputEvent(
                    0x43,
                    FunctionRowAction.DisplayBrightnessUp,
                    FunctionRowInputPhase.Up),
            ],
            published);
    }

    [Fact]
    public void PassThroughDoesNotPublishFunctionRowInput()
    {
        var state = new RazerBladeFunctionRowInputState();
        var published = new List<FunctionRowInputEvent>();

        bool suppress = FunctionRowKeyboardHookProcessor.Process(
            state,
            RazerBladeInputDeviceKind.Rz09_0581,
            scanCode: 0x43,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: false,
            published.Add);

        Assert.False(suppress);
        Assert.Empty(published);
    }

    private static bool Process(
        RazerBladeFunctionRowInputState state,
        bool keyDown,
        Action<FunctionRowInputEvent> publish) =>
        FunctionRowKeyboardHookProcessor.Process(
            state,
            RazerBladeInputDeviceKind.Rz09_0581,
            scanCode: 0x43,
            keyDown,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            publish);
}
