using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class Blade18DefaultFnInputStateTests
{
    public static TheoryData<uint, bool, int> Mappings => new()
    {
        { 0x19, false, (int)Blade18DefaultFnAction.Performance },
        { 0x13, false, (int)Blade18DefaultFnAction.RefreshRate },
        { 0x14, false, (int)Blade18DefaultFnAction.Touchpad },
        { 0x3B, false, (int)Blade18DefaultFnAction.VolumeMute },
        { 0x3C, false, (int)Blade18DefaultFnAction.VolumeDown },
        { 0x3D, false, (int)Blade18DefaultFnAction.VolumeUp },
        { 0x3E, false, (int)Blade18DefaultFnAction.Project },
        { 0x3F, false, (int)Blade18DefaultFnAction.PreviousTrack },
        { 0x40, false, (int)Blade18DefaultFnAction.PlayPause },
        { 0x41, false, (int)Blade18DefaultFnAction.NextTrack },
        { 0x42, false, (int)Blade18DefaultFnAction.DisplayBrightnessDown },
        { 0x43, false, (int)Blade18DefaultFnAction.DisplayBrightnessUp },
        { 0x44, false, (int)Blade18DefaultFnAction.KeyboardBrightnessDown },
        { 0x57, false, (int)Blade18DefaultFnAction.KeyboardBrightnessUp },
        { 0x58, false, (int)Blade18DefaultFnAction.PrintScreen },
        { 0x48, true, (int)Blade18DefaultFnAction.PageUp },
        { 0x4B, true, (int)Blade18DefaultFnAction.Home },
        { 0x50, true, (int)Blade18DefaultFnAction.PageDown },
        { 0x4D, true, (int)Blade18DefaultFnAction.End },
        { 0x53, true, (int)Blade18DefaultFnAction.Insert },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void MapsExactDefaultLayerAndConsumesThroughRelease(
        uint scanCode,
        bool extended,
        int expectedValue)
    {
        var state = new Blade18DefaultFnInputState();

        Assert.True(Process(
            state,
            scanCode,
            extended,
            keyDown: true,
            keyWasAlreadyDown: false,
            functionHeld: true,
            admitted: true,
            out Blade18DefaultFnInputEvent? down));
        Assert.Equal(
            new Blade18DefaultFnInputEvent(
                scanCode,
                (Blade18DefaultFnAction)expectedValue,
                FunctionRowInputPhase.Down),
            down);

        Assert.True(Process(
            state,
            scanCode,
            extended,
            keyDown: false,
            keyWasAlreadyDown: false,
            functionHeld: false,
            admitted: false,
            out Blade18DefaultFnInputEvent? up));
        Assert.Equal(
            new Blade18DefaultFnInputEvent(
                scanCode,
                (Blade18DefaultFnAction)expectedValue,
                FunctionRowInputPhase.Up),
            up);
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public void ExactExtendedIdentityIsRequired(
        uint scanCode,
        bool extended,
        int _)
    {
        var state = new Blade18DefaultFnInputState();

        Assert.False(Process(
            state,
            scanCode,
            !extended,
            keyDown: true,
            keyWasAlreadyDown: false,
            functionHeld: true,
            admitted: true,
            out Blade18DefaultFnInputEvent? inputEvent));
        Assert.Null(inputEvent);
    }

    [Fact]
    public void FunctionPressedAfterHeldKeyDoesNotActivateShortcut()
    {
        var state = new Blade18DefaultFnInputState();

        Assert.False(ProcessDown(state, 0x19, functionHeld: false, out _));
        Assert.False(Process(
            state,
            0x19,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out Blade18DefaultFnInputEvent? repeat));
        Assert.Null(repeat);
        Assert.False(ProcessUp(state, 0x19, out Blade18DefaultFnInputEvent? up));
        Assert.Null(up);
    }

    [Fact]
    public void UnknownHeldFirstEdgeIsLatchedAsPassThrough()
    {
        var state = new Blade18DefaultFnInputState();

        Assert.False(Process(
            state,
            0x13,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out Blade18DefaultFnInputEvent? inputEvent));
        Assert.Null(inputEvent);
        Assert.False(ProcessUp(state, 0x13, out _));
    }

    [Fact]
    public void AdmissionDecisionIsLatchedUntilRelease()
    {
        var state = new Blade18DefaultFnInputState();

        Assert.False(Process(
            state,
            0x14,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: false,
            functionHeld: true,
            admitted: false,
            out _));
        Assert.False(Process(
            state,
            0x14,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out _));
        Assert.False(ProcessUp(state, 0x14, out _));
    }

    [Theory]
    [InlineData(0x3C, false)]
    [InlineData(0x42, false)]
    [InlineData(0x48, true)]
    [InlineData(0x53, true)]
    public void RepeatableActionsPublishRepeat(uint scanCode, bool extended)
    {
        var state = new Blade18DefaultFnInputState();
        Assert.True(ProcessDown(state, scanCode, extended, true, out Blade18DefaultFnInputEvent? down));

        Assert.True(Process(
            state,
            scanCode,
            extended,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: false,
            admitted: false,
            out Blade18DefaultFnInputEvent? repeat));

        Assert.Equal(down?.Action, repeat?.Action);
        Assert.Equal(FunctionRowInputPhase.Repeat, repeat?.Phase);
    }

    [Theory]
    [InlineData(0x19)]
    [InlineData(0x13)]
    [InlineData(0x14)]
    [InlineData(0x3B)]
    [InlineData(0x3E)]
    [InlineData(0x3F)]
    [InlineData(0x40)]
    [InlineData(0x41)]
    [InlineData(0x58)]
    public void OneShotActionsConsumeRepeatWithoutPublishing(uint scanCode)
    {
        var state = new Blade18DefaultFnInputState();
        Assert.True(ProcessDown(state, scanCode, functionHeld: true, out _));

        Assert.True(Process(
            state,
            scanCode,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out Blade18DefaultFnInputEvent? repeat));
        Assert.Null(repeat);
    }

    [Fact]
    public void CancelKeepsConsumedPressSuppressedUntilReleaseWithoutEvents()
    {
        var state = new Blade18DefaultFnInputState();
        Assert.True(ProcessDown(state, 0x13, functionHeld: true, out _));

        state.Cancel();

        Assert.True(Process(
            state,
            0x13,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out Blade18DefaultFnInputEvent? repeat));
        Assert.Null(repeat);
        Assert.True(ProcessUp(state, 0x13, out Blade18DefaultFnInputEvent? up));
        Assert.Null(up);
    }

    [Fact]
    public void CancelPreservesPassThroughLatch()
    {
        var state = new Blade18DefaultFnInputState();
        Assert.False(ProcessDown(state, 0x19, functionHeld: false, out _));

        state.Cancel();

        Assert.False(Process(
            state,
            0x19,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out _));
        Assert.False(ProcessUp(state, 0x19, out _));
    }

    [Fact]
    public void FreshPhysicalDownReplacesStaleDecision()
    {
        var state = new Blade18DefaultFnInputState();
        Assert.False(ProcessDown(state, 0x19, functionHeld: false, out _));

        Assert.True(ProcessDown(
            state,
            0x19,
            functionHeld: true,
            out Blade18DefaultFnInputEvent? down));
        Assert.Equal(Blade18DefaultFnAction.Performance, down?.Action);
        Assert.Equal(FunctionRowInputPhase.Down, down?.Phase);
    }

    [Fact]
    public void UnknownScanCodePassesThroughWithoutLatch()
    {
        var state = new Blade18DefaultFnInputState();

        Assert.False(ProcessDown(state, 0x20, functionHeld: true, out _));
        Assert.False(ProcessUp(state, 0x20, out _));
    }

    [Fact]
    public void MissedMappedDownLatchesPassThroughUntilRelease()
    {
        var state = new Blade18DefaultFnInputState();

        Assert.True(state.ReconcilePassedThroughEdge(
            0x19,
            extended: false,
            keyDown: true,
            out Blade18DefaultFnInputEvent? recovery));
        Assert.Null(recovery);
        Assert.False(Process(
            state,
            0x19,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out _));
        Assert.False(ProcessUp(state, 0x19, out _));
    }

    [Fact]
    public void MissedReleaseClosesConsumedPress()
    {
        var state = new Blade18DefaultFnInputState();
        Assert.True(ProcessDown(state, 0x42, functionHeld: true, out _));

        Assert.True(state.ReconcilePassedThroughEdge(
            0x42,
            extended: false,
            keyDown: false,
            out Blade18DefaultFnInputEvent? release));
        Assert.Equal(
            new Blade18DefaultFnInputEvent(
                0x42,
                Blade18DefaultFnAction.DisplayBrightnessDown,
                FunctionRowInputPhase.Up),
            release);
        Assert.False(ProcessUp(state, 0x42, out _));
    }

    [Fact]
    public void ForeignPressCancellationKeepsSuppressionWithoutAction()
    {
        var state = new Blade18DefaultFnInputState();
        Assert.True(ProcessDown(state, 0x13, functionHeld: true, out _));

        state.CancelPress(0x13, extended: false);

        Assert.True(Process(
            state,
            0x13,
            extended: false,
            keyDown: true,
            keyWasAlreadyDown: true,
            functionHeld: true,
            admitted: true,
            out Blade18DefaultFnInputEvent? repeat));
        Assert.Null(repeat);
        Assert.True(ProcessUp(state, 0x13, out Blade18DefaultFnInputEvent? up));
        Assert.Null(up);
    }

    [Fact]
    public void UnmappedEdgeDoesNotRequestHookRecovery()
    {
        var state = new Blade18DefaultFnInputState();

        Assert.False(state.ReconcilePassedThroughEdge(
            0x20,
            extended: false,
            keyDown: true,
            out Blade18DefaultFnInputEvent? inputEvent));
        Assert.Null(inputEvent);
    }

    private static bool ProcessDown(
        Blade18DefaultFnInputState state,
        uint scanCode,
        bool functionHeld,
        out Blade18DefaultFnInputEvent? inputEvent) => ProcessDown(
            state,
            scanCode,
            extended: false,
            functionHeld,
            out inputEvent);

    private static bool ProcessDown(
        Blade18DefaultFnInputState state,
        uint scanCode,
        bool extended,
        bool functionHeld,
        out Blade18DefaultFnInputEvent? inputEvent) => Process(
            state,
            scanCode,
            extended,
            keyDown: true,
            keyWasAlreadyDown: false,
            functionHeld,
            admitted: true,
            out inputEvent);

    private static bool ProcessUp(
        Blade18DefaultFnInputState state,
        uint scanCode,
        out Blade18DefaultFnInputEvent? inputEvent) => Process(
            state,
            scanCode,
            extended: false,
            keyDown: false,
            keyWasAlreadyDown: false,
            functionHeld: false,
            admitted: false,
            out inputEvent);

    private static bool Process(
        Blade18DefaultFnInputState state,
        uint scanCode,
        bool extended,
        bool keyDown,
        bool keyWasAlreadyDown,
        bool functionHeld,
        bool admitted,
        out Blade18DefaultFnInputEvent? inputEvent) => state.Process(
            scanCode,
            extended,
            keyDown,
            keyWasAlreadyDown,
            functionHeld,
            admitted,
            out inputEvent);
}
