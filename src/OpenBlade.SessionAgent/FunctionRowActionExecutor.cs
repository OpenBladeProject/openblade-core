using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IDisplayBrightnessController
{
    Task<byte?> AdjustAsync(int direction, CancellationToken cancellationToken);
}

public interface IDisplayBrightnessOsd : IDisposable
{
    void Show(byte brightness);
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
    private readonly IDisplayBrightnessOsd displayBrightnessOsd;
    private readonly Dictionary<uint, DisplayBrightnessRoute> displayBrightnessRoutes = [];

    public FunctionRowActionExecutor(
        IServiceClient serviceClient,
        ISpecialKeyInputSender input,
        IDisplayBrightnessController displayBrightness,
        IDisplayBrightnessOsd displayBrightnessOsd)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.displayBrightness = displayBrightness
            ?? throw new ArgumentNullException(nameof(displayBrightness));
        this.displayBrightnessOsd = displayBrightnessOsd
            ?? throw new ArgumentNullException(nameof(displayBrightnessOsd));
    }

    public async Task ExecuteAsync(
        FunctionRowInputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(inputEvent.Action)
            || !Enum.IsDefined(inputEvent.Phase))
        {
            throw new ArgumentOutOfRangeException(nameof(inputEvent));
        }

        try
        {
            await ExecuteCoreAsync(inputEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Function-row action {inputEvent.Action} failed: {exception.Message}",
                exception);
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        bool nativePressed = displayBrightnessRoutes.Values.Any(
            route => route is DisplayBrightnessRoute.Native
                or DisplayBrightnessRoute.NativeReleaseRequired);
        if (!nativePressed)
        {
            displayBrightnessRoutes.Clear();
            return;
        }

        ControlOperationResult result = await serviceClient
            .SetDisplayBrightnessHidUsageAsync(
                DisplayBrightnessHidUsage.Released,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureApplied(result);
        displayBrightnessRoutes.Clear();
    }

    private async Task ExecuteCoreAsync(
        FunctionRowInputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        FunctionRowAction action = inputEvent.Action;
        if (action is FunctionRowAction.DisplayBrightnessDown
            or FunctionRowAction.DisplayBrightnessUp)
        {
            await ExecuteDisplayBrightnessAsync(inputEvent, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (inputEvent.Phase == FunctionRowInputPhase.Up)
        {
            return;
        }

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
                throw new InvalidOperationException("The function-row action was not recognized.");
        }
    }

    private async Task ExecuteDisplayBrightnessAsync(
        FunctionRowInputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        int direction = inputEvent.Action == FunctionRowAction.DisplayBrightnessDown
            ? -1
            : 1;
        if (inputEvent.Phase == FunctionRowInputPhase.Down)
        {
            uint? priorNativeScanCode = displayBrightnessRoutes
                .Where(pair => pair.Value is DisplayBrightnessRoute.Native
                    or DisplayBrightnessRoute.NativeReleaseRequired)
                .Select(pair => (uint?)pair.Key)
                .FirstOrDefault();
            if (priorNativeScanCode is { } prior
                && prior != inputEvent.ScanCode)
            {
                await ReleaseNativeAsync(prior, cancellationToken).ConfigureAwait(false);
            }

            DisplayBrightnessHidUsage usage = direction < 0
                ? DisplayBrightnessHidUsage.Decrement
                : DisplayBrightnessHidUsage.Increment;
            displayBrightnessRoutes[inputEvent.ScanCode] =
                DisplayBrightnessRoute.NativeReleaseRequired;
            ControlOperationResult result;
            try
            {
                result = await serviceClient
                    .SetDisplayBrightnessHidUsageAsync(usage, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await TryNeutralizeIndeterminatePressAsync(
                        inputEvent.ScanCode,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
            if (result.Status == ControlOperationStatus.Applied)
            {
                displayBrightnessRoutes[inputEvent.ScanCode] =
                    DisplayBrightnessRoute.Native;
                return;
            }
            if (result.Status == ControlOperationStatus.Blocked)
            {
                displayBrightnessRoutes.Remove(inputEvent.ScanCode);
                displayBrightnessRoutes[inputEvent.ScanCode] =
                    DisplayBrightnessRoute.Fallback;
                await AdjustDisplayBrightnessAsync(direction, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await TryNeutralizeIndeterminatePressAsync(
                    inputEvent.ScanCode,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(result.Message);
        }

        if (!displayBrightnessRoutes.TryGetValue(
                inputEvent.ScanCode,
                out DisplayBrightnessRoute route))
        {
            return;
        }

        if (inputEvent.Phase == FunctionRowInputPhase.Repeat)
        {
            if (route == DisplayBrightnessRoute.Fallback)
            {
                await AdjustDisplayBrightnessAsync(direction, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                DisplayBrightnessHidUsage usage = direction < 0
                    ? DisplayBrightnessHidUsage.Decrement
                    : DisplayBrightnessHidUsage.Increment;
                displayBrightnessRoutes[inputEvent.ScanCode] =
                    DisplayBrightnessRoute.NativeReleaseRequired;
                ControlOperationResult result = await serviceClient
                    .SetDisplayBrightnessHidUsageAsync(usage, cancellationToken)
                    .ConfigureAwait(false);
                EnsureApplied(result);
                displayBrightnessRoutes[inputEvent.ScanCode] = DisplayBrightnessRoute.Native;
            }
            return;
        }

        if (route is DisplayBrightnessRoute.Native
            or DisplayBrightnessRoute.NativeReleaseRequired)
        {
            await ReleaseNativeAsync(inputEvent.ScanCode, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            displayBrightnessRoutes.Remove(inputEvent.ScanCode);
        }
    }

    private async Task TryNeutralizeIndeterminatePressAsync(
        uint scanCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReleaseNativeAsync(scanCode, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Keep NativeReleaseRequired latched. The physical key-up and the
            // pump reset path will retry the neutral report.
        }
    }

    private async Task ReleaseNativeAsync(
        uint scanCode,
        CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient
            .SetDisplayBrightnessHidUsageAsync(
                DisplayBrightnessHidUsage.Released,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureApplied(result);
        displayBrightnessRoutes.Remove(scanCode);
    }

    private async Task AdjustDisplayBrightnessAsync(
        int direction,
        CancellationToken cancellationToken)
    {
        byte? brightness = await displayBrightness.AdjustAsync(
            direction,
            cancellationToken).ConfigureAwait(false);
        if (brightness is { } value)
        {
            displayBrightnessOsd.Show(value);
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

    private static void EnsureApplied(ControlOperationResult result)
    {
        if (result.Status != ControlOperationStatus.Applied)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private enum DisplayBrightnessRoute
    {
        Native,
        NativeReleaseRequired,
        Fallback,
    }
}
