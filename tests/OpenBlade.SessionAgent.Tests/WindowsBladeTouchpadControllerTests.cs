using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsBladeTouchpadControllerTests
{
    [Fact]
    public void ConstructorRejectsMissingInputSender()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WindowsBladeTouchpadController(null!));
    }

    [Fact]
    public void DocumentedTouchpadShortcutParsesToControlWindowsF24()
    {
        (ushort key, ushort[] modifiers) =
            SpecialKeyShortcutParser.Parse("Ctrl+Win+F24");

        Assert.Equal(0x87, key);
        Assert.Equal([0x11, 0x5B], modifiers);
    }

    [Fact]
    public async Task ToggleSendsDocumentedWindowsShortcutOnce()
    {
        var input = new RecordingInputSender();
        var controller = new WindowsBladeTouchpadController(input);

        await controller.ToggleAsync(CancellationToken.None);

        Assert.Equal(["Ctrl+Win+F24"], input.Shortcuts);
    }

    [Fact]
    public async Task CanceledToggleDoesNotSendInput()
    {
        var input = new RecordingInputSender();
        var controller = new WindowsBladeTouchpadController(input);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.ToggleAsync(cancellation.Token));

        Assert.Empty(input.Shortcuts);
    }

    private sealed class RecordingInputSender : ISpecialKeyInputSender
    {
        public List<string> Shortcuts { get; } = [];

        public void SendKeyboardShortcut(string shortcut) => Shortcuts.Add(shortcut);

        public void SendText(string text) => throw new NotSupportedException();

        public void SendMouseAction(SpecialKeyActionKind action) =>
            throw new NotSupportedException();

        public void SendMediaAction(SpecialKeyActionKind action) =>
            throw new NotSupportedException();

        public void ToggleMicrophoneMute() => throw new NotSupportedException();

        public void Launch(string target, bool requireWebAddress) =>
            throw new NotSupportedException();
    }
}
