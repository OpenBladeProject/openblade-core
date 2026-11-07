using System.Globalization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private LightingSettings? independentLightingWrapper;
    private IndependentLightingSettings? independentLightingDelta;
    private byte? independentBrightness;
    private LogoLightingMode? independentLogo;
    private PowerSource independentLightingSource;
    private long independentLightingCommitGeneration;
    private IReadOnlyList<LogoLightingMode> independentLogoDisplayModes = [];

    public bool UsesIndependentLightingControls => Capabilities.SupportsIndependentLightingControls;
    public bool IsKeyboardEffectSelectionVisible => !UsesIndependentLightingControls;
    public bool ShowLightingEffectLabControls => LabFeaturesEnabled && !UsesIndependentLightingControls;
    public long LightingCommitGeneration => UsesIndependentLightingControls ? independentLightingCommitGeneration : 0;
    public bool IsLightingCommitCurrent(LightingCommitRequest request) => !UsesIndependentLightingControls
        || independentLightingDelta is not null && request.EditorGeneration == independentLightingCommitGeneration
            && request.Lighting == IndependentLightingDraft;
    public string LogoLightingPlaceholderText => "—";
    public int LightingBrightnessMinimum => UsesIndependentLightingControls ? 0 : 1;
    public bool HasLightingBrightnessValue => !UsesIndependentLightingControls || independentBrightness.HasValue;
    public string LightingBrightnessValueText => HasLightingBrightnessValue
        ? LightingBrightnessEditor.ToString(CultureInfo.CurrentCulture) : "—";

    private string FormatPresentedLightingProfile(LightingSettings lighting) =>
        UsesIndependentLightingControls && lighting.IndependentControls is null
            ? TrayText.Get("Not set") : LightingPresentation.FormatLightingProfile(lighting);

    private IReadOnlyList<LogoLightingMode> PreserveIndependentLogoPresentation(
        DeviceCapabilities capabilities, IReadOnlyList<LogoLightingMode> admitted,
        IReadOnlyList<LogoLightingMode> presented)
    {
        if (!capabilities.SupportsIndependentLightingControls)
        {
            independentLogoDisplayModes = [];
            return presented;
        }
        if (admitted.Count > 0)
        {
            independentLogoDisplayModes = admitted;
        }
        else if (!ExperimentalWritesPaused)
        {
            independentLogoDisplayModes = [];
        }
        // Retain previously admitted choices as disabled presentation through a
        // recovery pause. Current capability admission still governs every edit.
        return ExperimentalWritesPaused && admitted.Count == 0 ? independentLogoDisplayModes : presented;
    }

    private LightingSettings IndependentLightingDraft =>
        (independentLightingWrapper ?? SelectedLighting) with
        {
            IndependentControls = MergeIndependentLighting(
                independentLightingWrapper?.IndependentControls, independentLightingDelta),
        };

    private static IndependentLightingSettings? MergeIndependentLighting(
        IndependentLightingSettings? baseline, IndependentLightingSettings? update) =>
        update is null ? baseline : new(update.Brightness ?? baseline?.Brightness,
            update.LogoMode ?? baseline?.LogoMode);

    private bool CanSubmitLightingDraft(LightingSettings value) =>
        ProfileAdmissionPolicy.LightingSupported(value, CommandCapabilities)
        && (!UsesIndependentLightingControls
            || independentLightingDelta is not null && value == IndependentLightingDraft
                && value.IndependentControls is { IsWellFormed: true }
                && (value with { IndependentControls = null })
                    == ((independentLightingWrapper ?? SelectedLighting) with { IndependentControls = null }));

    private void SetIndependentBrightness(int value)
    {
        if (value is < 0 or > 100 || independentBrightness == value)
        {
            return;
        }

        independentBrightness = (byte)value;
        independentLightingDelta = new((byte)value, independentLightingDelta?.LogoMode);
        NotifyIndependentLightingEdited();
    }

    private void SetIndependentLogo(LogoLightingMode? value)
    {
        if (value is not { } logo || !Capabilities.LogoLightingModes.Contains(logo)
            || independentLogo == logo)
        {
            return;
        }

        independentLogo = logo;
        independentLightingDelta = new(independentLightingDelta?.Brightness, logo);
        NotifyIndependentLightingEdited();
    }

    private void NotifyIndependentLightingEdited()
    {
        independentLightingCommitGeneration++;
        Notify(nameof(LightingCommitGeneration));
        NotifyIndependentLightingPresentation();
        NotifyComputed(nameof(LightingDraft), LightingDraft);
        NotifyComputed(nameof(IsLightingApplyPending), IsLightingApplyPending);
        NotifyCommands();
    }

    private void NotifyIndependentLightingPresentation()
    {
        Notify(nameof(UsesIndependentLightingControls));
        Notify(nameof(IsKeyboardEffectSelectionVisible));
        Notify(nameof(ShowLightingEffectLabControls));
        Notify(nameof(LightingBrightnessMinimum));
        Notify(nameof(HasLightingBrightnessValue));
        Notify(nameof(LightingBrightnessValueText));
        Notify(nameof(LightingBrightnessEditor));
        Notify(nameof(LogoLightingModeEditor));
        Notify(nameof(SelectedLogoLightingModeOption));
    }

    private void LoadIndependentLightingEditor(
        LightingSettings wrapper, IndependentLightingSettings? observations)
    {
        independentLightingWrapper = wrapper;
        independentLightingDelta = null;
        independentBrightness = observations?.Brightness;
        independentLogo = observations?.LogoMode;
        independentLightingSource = ActivePowerSource;
        NotifyIndependentLightingEdited();
    }

    private void ApplyIndependentLightingPresentation(ServiceStatusSnapshot status, bool checking)
    {
        bool uncertain = status.ExperimentalWritesPaused
            || status.UnconfirmedWrites.Contains(DeviceSubsystem.Lighting);
        if (uncertain || independentLightingSource != status.ActivePowerSource)
        {
            independentLightingDelta = null;
            InvalidatePendingLightingHold();
        }
        // DesiredState may temporarily carry only the hardware patch being
        // confirmed. The override retains complete intent across field edits.
        SelectedLighting = status.LightingOverride?.Value ?? status.DesiredState.Lighting.Value;
        if (!checking && independentLightingDelta is null
            && (!IsLightingOperationInProgress || uncertain) && pendingLighting is null)
        {
            LoadIndependentLightingEditor(SelectedLighting, status.ConfirmedState?.IndependentLighting);
        }
        NotifyLightingParameterPresentation();
    }

    private Task<ControlOperationResult> ClearLightingOverrideAsync(CancellationToken token) =>
        UsesIndependentLightingControls
            ? serviceClient.SetIndependentLightingOverrideAsync(null, token)
            : serviceClient.SetLightingOverrideAsync(null, token);

    private async Task SetIndependentLightingAsync(LightingSettings requested, CancellationToken token)
    {
        IndependentLightingSettings? delta = requested == IndependentLightingDraft
            ? independentLightingDelta ?? requested.IndependentControls : requested.IndependentControls;
        if (delta is not { IsWellFormed: true } || !CanSubmitLightingDraft(requested))
        {
            return;
        }

        PowerSource sourceAtStart = ActivePowerSource;
        long revisionAtStart = latestAcceptedRevision;
        long connectionAtStart = connectionGeneration;
        InvalidatePendingLightingHold();
        long generationAtStart = lightingPresentationGeneration;
        ControlOperationStatus? resultStatus = null;
        bool operationAccepted = false;
        IsLightingOperationInProgress = true;
        try
        {
            operationAccepted = await RunOperationAsync(ct => serviceClient.SetIndependentLightingOverrideAsync(
                new ManualOverride<IndependentLightingSettings>(delta,
                    OverrideLifetime.UntilPowerSourceChange), ct), token, result =>
            {
                resultStatus = result.Status;
                if (result.Status is ControlOperationStatus.Applied or ControlOperationStatus.Stored
                    && lightingPresentationGeneration == generationAtStart)
                {
                    MarkActiveProfileDraft(sourceAtStart, lighting: requested);
                }
            });
        }
        finally
        {
            IsLightingOperationInProgress = false;
            if (!operationAccepted && Volatile.Read(ref disposed) == 0)
            {
                // Completion, cancellation and transport exceptions all stop the
                // queued commit. Resume is read-only; it must not replay this edit.
                await dispatcher.InvokeAsync(() =>
                {
                    independentLightingDelta = null;
                    InvalidatePendingLightingHold();
                    if (latestStatus is { } observation)
                    {
                        LoadIndependentLightingEditor(observation.LightingOverride?.Value
                            ?? observation.DesiredState.Lighting.Value, observation.ConfirmedState?.IndependentLighting);
                    }
                    else
                    {
                        NotifyIndependentLightingEdited();
                    }
                }, CancellationToken.None);
            }
        }
        if (!operationAccepted || resultStatus is not (ControlOperationStatus.Applied or ControlOperationStatus.Stored))
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (lightingPresentationGeneration != generationAtStart || IndependentLightingDraft != requested)
            {
                return;
            }

            independentLightingWrapper = requested;
            independentLightingDelta = null;
            if (resultStatus == ControlOperationStatus.Applied && IsConnected
                && connectionGeneration == connectionAtStart)
            {
                pendingLighting = new PendingLightingHold(requested, sourceAtStart, revisionAtStart,
                    timeProvider.GetUtcNow() + ControlPresentationHoldGracePeriod);
                if (latestStatus is { } status)
                {
                    ReleasePendingLightingHold(status);
                    if (pendingLighting is null)
                    {
                        ApplyIndependentLightingPresentation(status, IsCheckingLaptopConnection(status));
                    }
                }
            }
            NotifyIndependentLightingEdited();
        }, token);
    }
}
