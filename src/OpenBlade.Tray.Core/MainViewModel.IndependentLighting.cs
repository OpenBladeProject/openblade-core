using System.Globalization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private LightingSettings? independentLightingWrapper;
    private IndependentLightingSettings? independentLightingDelta;
    private byte? independentBrightness;
    private LogoLightingMode? independentLogo;
    private bool? independentVaporChamber;
    private bool? independentTouchpadIndicator;
    private PowerSource independentLightingSource;
    private long independentLightingCommitGeneration;
    private bool presentedIndependentLighting;
    private bool independentLightingEditorActive;
    private bool controlsKeyboardEffect;
    private IReadOnlyList<LogoLightingMode> independentLogoDisplayModes = [];

    public bool UsesIndependentLightingControls => Capabilities.SupportsIndependentLightingControls
        && (Capabilities.LightingEffects.Count == 0 || independentLightingEditorActive);
    public bool IsKeyboardEffectSelectionVisible => !Capabilities.SupportsIndependentLightingControls
        || Capabilities.LightingEffects.Count > 0;
    public bool ShowLightingEffectLabControls => IsKeyboardEffectSelectionVisible;
    public long LightingCommitGeneration => UsesIndependentLightingControls ? independentLightingCommitGeneration : 0;
    public bool IsLightingCommitCurrent(LightingCommitRequest request) => UsesIndependentLightingControls
        ? independentLightingDelta is not null
            && request.EditorGeneration == independentLightingCommitGeneration
            && request.Lighting == IndependentLightingDraft
        : request.Lighting?.IndependentControls is null
            || request.Lighting.ControlsKeyboardEffect && request.Lighting == LightingDraft;
    public string LogoLightingPlaceholderText => "—";
    public int LightingBrightnessMinimum => UsesIndependentLightingControls ? 0 : 1;
    public bool HasLightingBrightnessValue => !UsesIndependentLightingControls || independentBrightness.HasValue;
    public string LightingBrightnessValueText => HasLightingBrightnessValue
        ? LightingBrightnessEditor.ToString(CultureInfo.CurrentCulture) : "—";

    public bool CanEditVaporChamberLighting => Capabilities.SupportsVaporChamberLighting
        && CanRequestHardwareControl && VaporChamberLightingEnabledEditor.HasValue;
    public bool ShowVaporChamberLighting => Capabilities.SupportsVaporChamberLighting;
    public bool CanEditTouchpadStatusIndicator => Capabilities.SupportsTouchpadStatusIndicator
        && CanRequestHardwareControl && TouchpadStatusIndicatorEnabledEditor.HasValue;
    public bool? VaporChamberLightingEnabledEditor
    {
        get => UsesIndependentLightingControls ? independentVaporChamber
            : latestStatus?.ConfirmedState?.IndependentLighting?.VaporChamberLightingEnabled;
        set => SetIndependentZone(false, value);
    }
    public bool? TouchpadStatusIndicatorEnabledEditor
    {
        get => UsesIndependentLightingControls ? independentTouchpadIndicator
            : latestStatus?.ConfirmedState?.IndependentLighting?.TouchpadStatusIndicatorEnabled;
        set => SetIndependentZone(true, value);
    }

    private void SetIndependentZone(bool touchpadIndicator, bool? value)
    {
        if (value is not { } enabled || !(touchpadIndicator ? CanEditTouchpadStatusIndicator : CanEditVaporChamberLighting)
            || (touchpadIndicator ? TouchpadStatusIndicatorEnabledEditor : VaporChamberLightingEnabledEditor) == enabled)
        {
            return;
        }
        if (!UsesIndependentLightingControls)
        {
            LoadIndependentLightingEditor(SelectedLighting, latestStatus?.ConfirmedState?.IndependentLighting);
        }
        IndependentLightingSettings delta = independentLightingDelta ?? new();
        if (touchpadIndicator)
        {
            independentTouchpadIndicator = enabled;
            independentLightingDelta = delta with { TouchpadStatusIndicatorEnabled = enabled };
        }
        else
        {
            independentVaporChamber = enabled;
            independentLightingDelta = delta with { VaporChamberLightingEnabled = enabled };
        }
        NotifyIndependentLightingEdited();
    }

    private IReadOnlyList<string> ObservedOnlyLightingEffectLabels =>
        Capabilities.ObservedLightingEffects
            .Except(Capabilities.LightingEffects)
            .OrderBy(effect => effect)
            .Select(LightingPresentation.LightingEffectLabel)
            .ToArray();

    public bool ShowObservedOnlyLightingEffects => ObservedOnlyLightingEffectLabels.Count > 0;

    public string ObservedOnlyLightingEffectsText => ShowObservedOnlyLightingEffects
        ? TrayText.Format("Synapse lists these effects for this model; OpenBlade cannot control them yet: {0}",
            string.Join(Environment.NewLine, ObservedOnlyLightingEffectLabels.Select(label => $"• {label}")))
        : string.Empty;

    private string FormatPresentedLightingProfile(LightingSettings lighting) =>
        Capabilities.SupportsIndependentLightingControls && !lighting.ControlsKeyboardEffect
            && lighting.IndependentControls is null
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
            update.LogoMode ?? baseline?.LogoMode,
            update.VaporChamberLightingEnabled ?? baseline?.VaporChamberLightingEnabled,
            update.TouchpadStatusIndicatorEnabled ?? baseline?.TouchpadStatusIndicatorEnabled);

    private bool CanSubmitLightingDraft(LightingSettings value)
    {
        if (UsesIndependentLightingControls && value.IndependentControls is not null
            && value == IndependentLightingDraft)
        {
            // A field edit retains the wrapper's saved effect authority, but may
            // submit only the independently admitted delta, never change effect.
            return independentLightingDelta is not null
                && ProfileAdmissionPolicy.LightingSupported(value with { ControlsKeyboardEffect = false }, CommandCapabilities)
                && value.IndependentControls is { IsWellFormed: true }
                && (value with { IndependentControls = null })
                    == ((independentLightingWrapper ?? SelectedLighting) with { IndependentControls = null });
        }

        return ProfileAdmissionPolicy.LightingSupported(value, CommandCapabilities)
            && (!Capabilities.SupportsIndependentLightingControls || value.ControlsKeyboardEffect)
            && (value.IndependentControls is null
                || value.ControlsKeyboardEffect && !UsesIndependentLightingControls && value == LightingDraft)
            && (!UsesIndependentLightingControls
                || value.ControlsKeyboardEffect && Capabilities.SupportsIndependentLightingControls);
    }

    private bool PrepareFullLightingEffect(LightingEffect effect)
    {
        if (!Capabilities.SupportsIndependentLightingControls)
        {
            return true;
        }
        if (!Capabilities.LightingEffects.Contains(effect))
        {
            return false;
        }
        if (UsesIndependentLightingControls)
        {
            // The effect takes over keyboard brightness and logo. Keep the
            // configured zone fields, including any pending zone edits.
            IndependentLightingSettings? zones = IndependentLightingDraft.IndependentControls is { } fields
                ? new(VaporChamberLightingEnabled: fields.VaporChamberLightingEnabled,
                    TouchpadStatusIndicatorEnabled: fields.TouchpadStatusIndicatorEnabled)
                : null;
            LightingSettings baseline = (independentLightingWrapper ?? SelectedLighting) with
            {
                IndependentControls = zones is { IsWellFormed: true } ? zones : null,
                ControlsKeyboardEffect = true,
                Brightness = independentBrightness ?? 50,
                LogoMode = independentLogo,
            };
            independentLightingEditorActive = false;
            controlsKeyboardEffect = true;
            independentLightingDelta = null;
            independentLightingCommitGeneration++;
            lightingEditor.Load(LightingSettingsFactory.NormalizeLegacyOff(baseline));
            lightingEditor.SetEffectUnconfirmed(true);
            NotifyIndependentLightingPresentation();
        }
        return true;
    }

    private bool ShouldPresentIndependentLighting(ServiceStatusSnapshot status) =>
        status.Capabilities?.SupportsIndependentLightingControls == true
        && (status.Capabilities.LightingEffects.Count == 0
            || independentLightingDelta is not null
            || !lightingEditor.IsDirty && !IsLightingOperationInProgress && pendingLighting is null
                && (!(status.LightingOverride?.Value ?? status.DesiredState.Lighting.Value).ControlsKeyboardEffect
                    || status.ConfirmedState?.Lighting is null));

    private void SetIndependentBrightness(int value)
    {
        if (value is < 0 or > 100 || independentBrightness == value)
        {
            return;
        }

        independentBrightness = (byte)value;
        independentLightingDelta = (independentLightingDelta ?? new()) with { Brightness = (byte)value };
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
        independentLightingDelta = (independentLightingDelta ?? new()) with { LogoMode = logo };
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
        bool independent = UsesIndependentLightingControls;
        bool backendChanged = presentedIndependentLighting != independent;
        presentedIndependentLighting = independent;
        NotifyComputed(nameof(UsesIndependentLightingControls), independent);
        NotifyComputed(nameof(IsKeyboardEffectSelectionVisible), IsKeyboardEffectSelectionVisible);
        NotifyComputed(nameof(ShowLightingEffectLabControls), ShowLightingEffectLabControls);
        NotifyComputed(nameof(LightingBrightnessMinimum), LightingBrightnessMinimum);
        NotifyComputed(nameof(HasLightingBrightnessValue), HasLightingBrightnessValue);
        NotifyComputed(nameof(LightingBrightnessValueText), LightingBrightnessValueText);
        NotifyComputed(nameof(VaporChamberLightingEnabledEditor), VaporChamberLightingEnabledEditor);
        NotifyComputed(nameof(TouchpadStatusIndicatorEnabledEditor), TouchpadStatusIndicatorEnabledEditor);
        NotifyComputed(nameof(CanEditVaporChamberLighting), CanEditVaporChamberLighting);
        NotifyComputed(nameof(CanEditTouchpadStatusIndicator), CanEditTouchpadStatusIndicator);
        // Classic editor events already own their value notifications. Refresh
        // these getters only for independent values or an actual backend switch.
        if (independent || backendChanged)
        {
            NotifyComputed(nameof(LightingBrightnessEditor), LightingBrightnessEditor);
            NotifyComputed(nameof(LogoLightingModeEditor), LogoLightingModeEditor);
            NotifyComputed(nameof(SelectedLogoLightingModeOption), SelectedLogoLightingModeOption);
        }
    }

    private void LoadIndependentLightingEditor(
        LightingSettings wrapper, IndependentLightingSettings? observations)
    {
        independentLightingEditorActive = true;
        independentLightingWrapper = wrapper;
        independentLightingDelta = null;
        independentBrightness = observations?.Brightness;
        independentLogo = observations?.LogoMode;
        independentVaporChamber = observations?.VaporChamberLightingEnabled;
        independentTouchpadIndicator = observations?.TouchpadStatusIndicatorEnabled;
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

        LightingSettings fieldIntent = requested with { ControlsKeyboardEffect = false };
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
                    MarkActiveProfileDraft(sourceAtStart, lighting: fieldIntent);
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

            independentLightingWrapper = fieldIntent;
            independentLightingDelta = null;
            if (resultStatus == ControlOperationStatus.Applied && IsConnected
                && connectionGeneration == connectionAtStart)
            {
                pendingLighting = new PendingLightingHold(fieldIntent, sourceAtStart, revisionAtStart,
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
