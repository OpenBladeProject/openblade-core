using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class FunctionRowInputTests
{
    [Theory]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0581, true)]
    [InlineData(RazerBladeInputDeviceKind.Rz09_0528, true)]
    [InlineData(RazerBladeInputDeviceKind.Unsupported, false)]
    public void DeviceAdmissionIsExplicit(
        RazerBladeInputDeviceKind deviceKind,
        bool expected)
    {
        Assert.Equal(
            expected,
            RazerBladeFunctionRowMediaInput.SupportsDevice(deviceKind));
    }

    public static TheoryData<uint, FunctionRowAction> ScanCodeMappings =>
        new()
        {
            { 0x3B, FunctionRowAction.VolumeMute },
            { 0x3C, FunctionRowAction.VolumeDown },
            { 0x3D, FunctionRowAction.VolumeUp },
            { 0x3E, FunctionRowAction.Project },
            { 0x3F, FunctionRowAction.PreviousTrack },
            { 0x40, FunctionRowAction.PlayPause },
            { 0x41, FunctionRowAction.NextTrack },
            { 0x42, FunctionRowAction.DisplayBrightnessDown },
            { 0x43, FunctionRowAction.DisplayBrightnessUp },
            { 0x44, FunctionRowAction.KeyboardBrightnessDown },
            { 0x57, FunctionRowAction.KeyboardBrightnessUp },
            { 0x58, FunctionRowAction.PrintScreen },
        };

    [Theory]
    [MemberData(nameof(ScanCodeMappings))]
    public void MapsEveryCapturedFunctionRowScanCode(
        uint scanCode,
        FunctionRowAction expected)
    {
        bool suppress = RazerBladeFunctionRowMediaInput.TryOverride(
            scanCode,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowAction? action);

        Assert.True(suppress);
        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData(FunctionKeyPrimaryMode.MultimediaKeys, false, true)]
    [InlineData(FunctionKeyPrimaryMode.MultimediaKeys, true, false)]
    [InlineData(FunctionKeyPrimaryMode.FunctionKeys, false, false)]
    [InlineData(FunctionKeyPrimaryMode.FunctionKeys, true, true)]
    public void OverridesOnlyTheLayerConfiguredForMediaActions(
        FunctionKeyPrimaryMode primaryMode,
        bool functionKeyPressed,
        bool expectedOverride)
    {
        bool suppress = RazerBladeFunctionRowMediaInput.TryOverride(
            scanCode: 0x3B,
            keyDown: true,
            functionKeyPressed,
            primaryMode,
            driverModeActive: true,
            out FunctionRowAction? action);

        Assert.Equal(expectedOverride, suppress);
        Assert.Equal(
            expectedOverride ? FunctionRowAction.VolumeMute : null,
            action);
    }

    [Fact]
    public void PassesThroughWhenDriverModeIsNotConfirmed()
    {
        bool suppress = RazerBladeFunctionRowMediaInput.TryOverride(
            scanCode: 0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: false,
            out FunctionRowAction? action);

        Assert.False(suppress);
        Assert.Null(action);
    }

    [Fact]
    public void PassesThroughWhenFunctionKeyModeIsUnknown()
    {
        bool suppress = RazerBladeFunctionRowMediaInput.TryOverride(
            scanCode: 0x3B,
            keyDown: true,
            functionKeyPressed: false,
            primaryMode: null,
            driverModeActive: true,
            out FunctionRowAction? action);

        Assert.False(suppress);
        Assert.Null(action);
    }

    [Fact]
    public void SuppressesKeyUpWithoutPublishingAnotherAction()
    {
        bool suppress = RazerBladeFunctionRowMediaInput.TryOverride(
            scanCode: 0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowAction? action);

        Assert.True(suppress);
        Assert.Null(action);
    }

    [Fact]
    public void PassesThroughUnsupportedScanCodes()
    {
        bool suppress = RazerBladeFunctionRowMediaInput.TryOverride(
            scanCode: 0x2A,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowAction? action);

        Assert.False(suppress);
        Assert.Null(action);
    }

    [Fact]
    public void StatefulInput_LatchesSuppressionThroughFnAndStatusChanges()
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.True(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: true,
            FunctionKeyPrimaryMode.FunctionKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? downEvent));
        Assert.Equal(
            new FunctionRowInputEvent(0x3B, FunctionRowAction.VolumeMute, FunctionRowInputPhase.Down),
            downEvent);

        Assert.True(state.Process(
            0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: false,
            out FunctionRowInputEvent? upEvent));
        Assert.Equal(
            new FunctionRowInputEvent(0x3B, FunctionRowAction.VolumeMute, FunctionRowInputPhase.Up),
            upEvent);
    }

    [Fact]
    public void StatefulInput_DoesNotSuppressAnUnmatchedKeyUp()
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.False(state.Process(
            0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? inputEvent));
        Assert.Null(inputEvent);
    }

    [Fact]
    public void StatefulInput_LatchesPassThroughThroughDriverStatusChanges()
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.False(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: false,
            out FunctionRowInputEvent? initial));
        Assert.Null(initial);

        Assert.False(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? repeated));
        Assert.Null(repeated);

        Assert.False(state.Process(
            0x3B,
            keyDown: false,
            functionKeyPressed: true,
            FunctionKeyPrimaryMode.FunctionKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? released));
        Assert.Null(released);

        Assert.True(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? nextPress));
        Assert.Equal(FunctionRowInputPhase.Down, nextPress?.Phase);
    }

    [Fact]
    public void StatefulInput_LatchesPassThroughThroughFnChanges()
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.False(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: true,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));
        Assert.False(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));
        Assert.False(state.Process(
            0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));
    }

    [Fact]
    public void StatefulInput_LatchesPassThroughAcrossDeviceAdmissionRecovery()
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.False(state.Process(
            RazerBladeInputDeviceKind.Unsupported,
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));
        Assert.False(state.Process(
            RazerBladeInputDeviceKind.Rz09_0581,
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));
        Assert.False(state.Process(
            RazerBladeInputDeviceKind.Rz09_0581,
            0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));

        Assert.True(state.Process(
            RazerBladeInputDeviceKind.Rz09_0581,
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? nextPress));
        Assert.Equal(FunctionRowInputPhase.Down, nextPress?.Phase);
    }

    [Fact]
    public void PassedThroughDown_RecoversHookWithoutPublishingMediaAction()
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.True(state.ReconcilePassedThroughEdge(
            0x3B,
            keyDown: true,
            out FunctionRowInputEvent? passedThrough));
        Assert.Null(passedThrough);

        Assert.False(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? repeated));
        Assert.Null(repeated);

        Assert.False(state.Process(
            0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));

        Assert.True(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? recovered));
        Assert.Equal(
            new FunctionRowInputEvent(
                0x3B,
                FunctionRowAction.VolumeMute,
                FunctionRowInputPhase.Down),
            recovered);
    }

    [Fact]
    public void PassedThroughRelease_ClosesPreviouslySuppressedInput()
    {
        var state = new RazerBladeFunctionRowInputState();
        Assert.True(state.Process(
            0x43,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));

        Assert.True(state.ReconcilePassedThroughEdge(
            0x43,
            keyDown: false,
            out FunctionRowInputEvent? release));
        Assert.Equal(
            new FunctionRowInputEvent(
                0x43,
                FunctionRowAction.DisplayBrightnessUp,
                FunctionRowInputPhase.Up),
            release);

        Assert.False(state.Process(
            0x43,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));
    }

    [Fact]
    public void UncorrelatedRepeat_PreservesSuppressionButPassesFinalRelease()
    {
        var state = new RazerBladeFunctionRowInputState();
        Assert.True(state.Process(
            0x43,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out _));

        Assert.True(state.ReconcilePassedThroughEdge(
            0x43,
            keyDown: true,
            out FunctionRowInputEvent? release));
        Assert.Null(release);

        Assert.True(state.Process(
            0x43,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? repeated));
        Assert.Equal(FunctionRowInputPhase.Repeat, repeated?.Phase);

        Assert.False(state.Process(
            0x43,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? released));
        Assert.Equal(FunctionRowInputPhase.Up, released?.Phase);
    }

    [Fact]
    public void UncorrelatedMappedEdge_RequestsHookRecoveryWithoutAction()
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.True(state.ReconcilePassedThroughEdge(
            0x3B,
            keyDown: true,
            out FunctionRowInputEvent? inputEvent));
        Assert.Null(inputEvent);
    }

    [Theory]
    [InlineData(FunctionRowAction.VolumeDown, 0x3C, true)]
    [InlineData(FunctionRowAction.DisplayBrightnessDown, 0x42, true)]
    [InlineData(FunctionRowAction.KeyboardBrightnessDown, 0x44, true)]
    [InlineData(FunctionRowAction.VolumeMute, 0x3B, false)]
    [InlineData(FunctionRowAction.Project, 0x3E, false)]
    [InlineData(FunctionRowAction.PlayPause, 0x40, false)]
    [InlineData(FunctionRowAction.PrintScreen, 0x58, false)]
    public void StatefulInput_RepeatsOnlyContinuousActions(
        FunctionRowAction expected,
        uint scanCode,
        bool shouldRepeat)
    {
        var state = new RazerBladeFunctionRowInputState();
        Assert.True(state.Process(
            scanCode,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowInputEvent? first));
        Assert.Equal(
            new FunctionRowInputEvent(scanCode, expected, FunctionRowInputPhase.Down),
            first);

        Assert.True(state.Process(
            scanCode,
            keyDown: true,
            functionKeyPressed: true,
            FunctionKeyPrimaryMode.FunctionKeys,
            driverModeActive: false,
            out FunctionRowInputEvent? repeated));
        Assert.Equal(
            shouldRepeat
                ? new FunctionRowInputEvent(scanCode, expected, FunctionRowInputPhase.Repeat)
                : null,
            repeated);
    }

    [Theory]
    [InlineData(FunctionKeyPrimaryMode.MultimediaKeys, false)]
    [InlineData(FunctionKeyPrimaryMode.FunctionKeys, true)]
    public void StatefulInput_DisplayBrightnessPublishesResolvedDownRepeatAndUp(
        FunctionKeyPrimaryMode primaryMode,
        bool functionKeyPressed)
    {
        var state = new RazerBladeFunctionRowInputState();

        Assert.True(state.Process(
            0x43,
            keyDown: true,
            functionKeyPressed,
            primaryMode,
            driverModeActive: true,
            out FunctionRowInputEvent? down));
        Assert.Equal(
            new FunctionRowInputEvent(
                0x43,
                FunctionRowAction.DisplayBrightnessUp,
                FunctionRowInputPhase.Down),
            down);

        Assert.True(state.Process(
            0x43,
            keyDown: true,
            functionKeyPressed: !functionKeyPressed,
            primaryMode == FunctionKeyPrimaryMode.MultimediaKeys
                ? FunctionKeyPrimaryMode.FunctionKeys
                : FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: false,
            out FunctionRowInputEvent? repeat));
        Assert.Equal(FunctionRowInputPhase.Repeat, repeat?.Phase);

        Assert.True(state.Process(
            0x43,
            keyDown: false,
            functionKeyPressed: !functionKeyPressed,
            primaryMode: null,
            driverModeActive: false,
            out FunctionRowInputEvent? up));
        Assert.Equal(
            new FunctionRowInputEvent(
                0x43,
                FunctionRowAction.DisplayBrightnessUp,
                FunctionRowInputPhase.Up),
            up);
    }
}
