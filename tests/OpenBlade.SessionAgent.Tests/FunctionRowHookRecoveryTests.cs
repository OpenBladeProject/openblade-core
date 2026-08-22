using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class FunctionRowHookRecoveryTests
{
    [Fact]
    public void MissedMediaDown_ReinstallsWithoutPublishingAction()
    {
        var state = new RazerBladeFunctionRowInputState();

        FunctionRowHookRecoveryResult result = Evaluate(
            state,
            observedByHook: false,
            keyDown: true);

        Assert.True(result.ReinstallHook);
        Assert.Null(result.ReleaseEvent);
        Assert.False(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));
    }

    [Fact]
    public void MatchedHookObservation_DoesNotEnterRecovery()
    {
        var state = new RazerBladeFunctionRowInputState();

        FunctionRowHookRecoveryResult result = Evaluate(
            state,
            observedByHook: true,
            keyDown: true);

        Assert.False(result.ReinstallHook);
        Assert.Null(result.ReleaseEvent);
        Assert.True(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? inputEvent));
        Assert.Equal(FunctionRowInputPhase.Down, inputEvent?.Phase);
    }

    [Fact]
    public void UnmatchedMappedTarget_ReinstallsWithoutPublishingAction()
    {
        FunctionRowHookRecoveryResult result =
            FunctionRowHookRecovery.EvaluateTargetPacket(
                new RazerBladeFunctionRowInputState(),
                RazerBladeInputDeviceKind.Rz09_0581,
                observedByHook: false,
                scanCode: 0x3B,
                keyDown: true);

        Assert.True(result.ReinstallHook);
        Assert.Null(result.ReleaseEvent);
    }

    [Theory]
    [InlineData(RazerBladeInputDeviceKind.Unsupported, 0x3B)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0581, 0x1E)]
    public void UnsupportedInput_DoesNotRequestRecovery(
        RazerBladeInputDeviceKind deviceKind,
        uint scanCode)
    {
        FunctionRowHookRecoveryResult result =
            FunctionRowHookRecovery.EvaluateTargetPacket(
                new RazerBladeFunctionRowInputState(),
                deviceKind,
                observedByHook: false,
                scanCode,
                keyDown: true);

        Assert.False(result.ReinstallHook);
        Assert.Null(result.ReleaseEvent);
    }

    [Fact]
    public void MissedRelease_ClosesPreviouslySuppressedInput()
    {
        var state = new RazerBladeFunctionRowInputState();
        Assert.True(state.Process(
            0x43,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));

        FunctionRowHookRecoveryResult result = FunctionRowHookRecovery.EvaluateTargetPacket(
            state,
            RazerBladeInputDeviceKind.Rz09_0581,
            observedByHook: false,
            scanCode: 0x43,
            keyDown: false);

        Assert.True(result.ReinstallHook);
        Assert.Equal(FunctionRowInputPhase.Up, result.ReleaseEvent?.Phase);
        Assert.Equal(FunctionRowAction.DisplayBrightnessUp, result.ReleaseEvent?.Action);
    }

    [Fact]
    public void UncorrelatedRepeat_PreservesPreviouslySuppressedInput()
    {
        var state = new RazerBladeFunctionRowInputState();
        Assert.True(state.Process(
            0x43,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));

        FunctionRowHookRecoveryResult result = FunctionRowHookRecovery.EvaluateTargetPacket(
            state,
            RazerBladeInputDeviceKind.Rz09_0581,
            observedByHook: false,
            scanCode: 0x43,
            keyDown: true);

        Assert.True(result.ReinstallHook);
        Assert.Null(result.ReleaseEvent);
        Assert.True(state.Process(
            0x43,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? release));
        Assert.Equal(FunctionRowInputPhase.Up, release?.Phase);
    }

    private static FunctionRowHookRecoveryResult Evaluate(
        RazerBladeFunctionRowInputState state,
        bool observedByHook,
        bool keyDown) =>
        FunctionRowHookRecovery.EvaluateTargetPacket(
            state,
            RazerBladeInputDeviceKind.Rz09_0581,
            observedByHook,
            scanCode: 0x3B,
            keyDown);
}
