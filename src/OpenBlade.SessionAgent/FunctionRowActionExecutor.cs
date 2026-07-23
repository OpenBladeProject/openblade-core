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
    private const byte KeyboardBrightnessStep = 10;

    private readonly IServiceClient serviceClient;
    private readonly ISpecialKeyInputSender input;
    private readonly IDisplayBrightnessController displayBrightness;
    private LightingSettings? lastVisibleLighting;

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
        ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        LightingSettings current = status.DesiredState.Lighting.Value;
        if (current.Effect != LightingEffect.Off && current.Brightness > 0)
        {
            lastVisibleLighting = current;
        }

        LightingSettings next;
        if (direction < 0)
        {
            if (current.Effect == LightingEffect.Off || current.Brightness == 0)
            {
                return;
            }

            if (current.Brightness <= KeyboardBrightnessStep)
            {
                next = LightingSettingsFactory.Off() with
                {
                    LogoMode = current.LogoMode,
                };
            }
            else
            {
                next = current with
                {
                    Brightness = (byte)(current.Brightness - KeyboardBrightnessStep),
                };
                lastVisibleLighting = next;
            }
        }
        else
        {
            if (current.Effect != LightingEffect.Off && current.Brightness >= 100)
            {
                return;
            }

            if (current.Effect == LightingEffect.Off || current.Brightness == 0)
            {
                LightingSettings baseline = ResolveVisibleBaseline(status);
                next = baseline with
                {
                    Brightness = KeyboardBrightnessStep,
                    LogoMode = current.LogoMode ?? baseline.LogoMode,
                };
            }
            else
            {
                next = current with
                {
                    Brightness = (byte)Math.Min(
                        100,
                        current.Brightness + KeyboardBrightnessStep),
                };
            }
            lastVisibleLighting = next;
        }

        ControlOperationResult result = await serviceClient.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                next,
                OverrideLifetime.UntilPowerSourceChange),
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(result);
    }

    private LightingSettings ResolveVisibleBaseline(ServiceStatusSnapshot status)
    {
        if (lastVisibleLighting is
            {
                Effect: not LightingEffect.Off,
                Brightness: > 0,
            } remembered)
        {
            return remembered;
        }

        if (status.ActivePowerSource is not PowerSource.ExternalUnknown)
        {
            LightingSettings profile = status.Profiles
                .ForPowerSource(status.ActivePowerSource)
                .Lighting;
            if (profile.Effect != LightingEffect.Off && profile.Brightness > 0)
            {
                return profile;
            }
        }

        return LightingSettingsFactory.Static(
            LightingSettingsFactory.DefaultVisibleColor,
            KeyboardBrightnessStep);
    }

    private static void EnsureAccepted(ControlOperationResult result)
    {
        if (result.Status is ControlOperationStatus.Failed or ControlOperationStatus.Blocked)
        {
            throw new InvalidOperationException(result.Message);
        }
    }
}
