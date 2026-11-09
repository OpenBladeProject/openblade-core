using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class FunctionRowBackendSelectionTests
{
    [Theory]
    [InlineData(KeyboardInputBackend.WindowsHook, KeyboardDriverModeKnowledge.Active, true)]
    [InlineData(KeyboardInputBackend.WindowsHook, KeyboardDriverModeKnowledge.Unknown, false)]
    [InlineData(KeyboardInputBackend.RazerDriver, KeyboardDriverModeKnowledge.Active, false)]
    [InlineData(KeyboardInputBackend.Unknown, KeyboardDriverModeKnowledge.Active, false)]
    [InlineData((KeyboardInputBackend)99, KeyboardDriverModeKnowledge.Active, false)]
    public void OnlySelectedWindowsHookAdmitsNewPress(
        KeyboardInputBackend backend, KeyboardDriverModeKnowledge knowledge, bool admitted)
    {
        Assert.Equal(admitted, FunctionRowKeyboardHookProcessor.ShouldAdmitNewPress(
            backend, new(knowledge, 0)));
    }

    [Fact]
    public void BackendChangeFinishesHeldMediaPressBeforeDriverCanAcquire()
    {
        var state = new RazerBladeFunctionRowInputState();
        var events = new List<FunctionRowInputEvent>();
        Assert.True(Process(true, true));
        Assert.True(state.HasActiveKeys);

        // A new selection closes admission to new presses, while the original
        // owner's repeat and release stay on its latched media route.
        Assert.True(Process(true, false, alreadyDown: true));
        Assert.True(state.HasActiveKeys);
        Assert.True(Process(false, false));
        Assert.False(state.HasActiveKeys);
        Assert.Equal(
            [FunctionRowInputPhase.Down, FunctionRowInputPhase.Repeat, FunctionRowInputPhase.Up],
            events.Select(e => e.Phase));

        Assert.False(Process(true, false));
        Assert.True(state.HasActiveKeys);
        Assert.False(Process(false, false));
        Assert.False(state.HasActiveKeys);

        bool Process(bool down, bool admitted, bool alreadyDown = false) =>
            FunctionRowKeyboardHookProcessor.Process(state, RazerBladeInputDeviceKind.Rz09_0581,
                0x3D, down, alreadyDown, false, FunctionKeyPrimaryMode.MultimediaKeys,
                admitted, events.Add);
    }
}
