using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IDisplayBrightnessController
{
    Task<bool> AdjustAsync(int direction, CancellationToken cancellationToken);
}

/// <summary>
/// Reconstructs the normal-mode behavior that the RZ09-0581 firmware stops
/// producing after OpenBlade enables keyboard Driver mode.
/// </summary>
public sealed class FunctionRowActionExecutor
{
    private readonly IServiceClient serviceClient;
    private readonly ISpecialKeyInputSender input;
    private readonly IDisplayBrightnessController displayBrightness;

    public FunctionRowActionExecutor(
        IServiceClient serviceClient,
        ISpecialKeyInputSender input,
        IDisplayBrightnessController displayBrightness)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.displayBrightness = displayBrightness
            ?? throw new ArgumentNullException(nameof(displayBrightness));
    }

    public async Task ExecuteAsync(
        FunctionRowAction action,
        CancellationToken cancellationToken)
    {
        switch (action)
        {
            case FunctionRowAction.VolumeMute:
                input.SendMediaAction(SpecialKeyActionKind.VolumeMute);
                return;
            case FunctionRowAction.VolumeDown:
                input.SendMediaAction(SpecialKeyActionKind.VolumeDown);
                return;
            case FunctionRowAction.VolumeUp:
                input.SendMediaAction(SpecialKeyActionKind.VolumeUp);
                return;
            case FunctionRowAction.Project:
                input.SendKeyboardShortcut("Win+P");
                return;
            case FunctionRowAction.PreviousTrack:
                input.SendMediaAction(SpecialKeyActionKind.MediaPreviousTrack);
                return;
            case FunctionRowAction.PlayPause:
                input.SendMediaAction(SpecialKeyActionKind.MediaPlayPause);
                return;
            case FunctionRowAction.NextTrack:
                input.SendMediaAction(SpecialKeyActionKind.MediaNextTrack);
                return;
            case FunctionRowAction.DisplayBrightnessDown:
                _ = await displayBrightness.AdjustAsync(
                    direction: -1,
                    cancellationToken).ConfigureAwait(false);
                return;
            case FunctionRowAction.DisplayBrightnessUp:
                _ = await displayBrightness.AdjustAsync(
                    direction: 1,
                    cancellationToken).ConfigureAwait(false);
                return;
            case FunctionRowAction.KeyboardBrightnessDown:
                await AdjustKeyboardBrightnessAsync(
                    direction: -1,
                    cancellationToken).ConfigureAwait(false);
                return;
            case FunctionRowAction.KeyboardBrightnessUp:
                await AdjustKeyboardBrightnessAsync(
                    direction: 1,
                    cancellationToken).ConfigureAwait(false);
                return;
            case FunctionRowAction.PrintScreen:
                input.SendKeyboardShortcut("PrintScreen");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private async Task AdjustKeyboardBrightnessAsync(
        int direction,
        CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient.StepKeyboardBrightnessAsync(
            direction < 0
                ? KeyboardBrightnessDirection.Decrease
                : KeyboardBrightnessDirection.Increase,
            OverrideLifetime.UntilPowerSourceChange,
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(result);
    }

    private static void EnsureAccepted(ControlOperationResult result)
    {
        if (result.Status is ControlOperationStatus.Failed or ControlOperationStatus.Blocked)
        {
            throw new InvalidOperationException(result.Message);
        }
    }
}
