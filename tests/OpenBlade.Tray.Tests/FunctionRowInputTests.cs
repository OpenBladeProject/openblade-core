using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class FunctionRowInputTests
{
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
        bool suppress = Rz09_0581FunctionRowMediaInput.TryOverride(
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
        bool suppress = Rz09_0581FunctionRowMediaInput.TryOverride(
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
        bool suppress = Rz09_0581FunctionRowMediaInput.TryOverride(
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
        bool suppress = Rz09_0581FunctionRowMediaInput.TryOverride(
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
        bool suppress = Rz09_0581FunctionRowMediaInput.TryOverride(
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
        bool suppress = Rz09_0581FunctionRowMediaInput.TryOverride(
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
        var state = new Rz09_0581FunctionRowInputState();

        Assert.True(state.Process(
            0x3B,
            keyDown: true,
            functionKeyPressed: true,
            FunctionKeyPrimaryMode.FunctionKeys,
            driverModeActive: true,
            out FunctionRowAction? downAction));
        Assert.Equal(FunctionRowAction.VolumeMute, downAction);

        Assert.True(state.Process(
            0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: false,
            out FunctionRowAction? upAction));
        Assert.Null(upAction);
    }

    [Fact]
    public void StatefulInput_DoesNotSuppressAnUnmatchedKeyUp()
    {
        var state = new Rz09_0581FunctionRowInputState();

        Assert.False(state.Process(
            0x3B,
            keyDown: false,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowAction? action));
        Assert.Null(action);
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
        var state = new Rz09_0581FunctionRowInputState();
        Assert.True(state.Process(
            scanCode,
            keyDown: true,
            functionKeyPressed: false,
            FunctionKeyPrimaryMode.MultimediaKeys,
            driverModeActive: true,
            out FunctionRowAction? first));
        Assert.Equal(expected, first);

        Assert.True(state.Process(
            scanCode,
            keyDown: true,
            functionKeyPressed: true,
            FunctionKeyPrimaryMode.FunctionKeys,
            driverModeActive: false,
            out FunctionRowAction? repeated));
        Assert.Equal(shouldRepeat ? expected : null, repeated);
    }
}
