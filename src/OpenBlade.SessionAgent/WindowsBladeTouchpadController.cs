namespace OpenBlade.SessionAgent;

/// <summary>
/// Toggles a Windows Precision Touchpad through the inbox keyboard contract.
/// See https://learn.microsoft.com/windows-hardware/design/component-guidelines/touchpad-enable-or-disable-toggle-button.
/// The exact Synapse SysUtilsNative.dll (SHA-256
/// 01223BFABF0355F42705836974E343A1F16F837D692999020D218B2615B8E318)
/// emits the same chord; the documented status registry value is read-only.
/// </summary>
public sealed class WindowsBladeTouchpadController : IBladeTouchpadController
{
    private const string ToggleShortcut = "Ctrl+Win+F24";

    private readonly ISpecialKeyInputSender input;

    public WindowsBladeTouchpadController(ISpecialKeyInputSender input)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public Task ToggleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        input.SendKeyboardShortcut(ToggleShortcut);
        return Task.CompletedTask;
    }
}
