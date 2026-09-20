using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal enum LightingEditorChangeKind
{
    ValueChanged,
    Refreshed,
    ParametersChanged,
    EditCompleted,
    TransitionCompleted,
}

internal enum LightingEditorValue
{
    Effect,
    Brightness,
    Red,
    Green,
    Blue,
    PrimaryColor,
    SecondaryRed,
    SecondaryGreen,
    SecondaryBlue,
    SecondaryColor,
    BreathingColorMode,
    ReactiveDuration,
    StarlightColorMode,
    StarlightDuration,
    RandomColors,
    Direction,
    RotationDirection,
    AmbientAwarenessRegion,
    AudioMeterColorBoost,
    AudioMeterDecayMilliseconds,
    LogoMode,
    Draft,
    Dirty,
    EffectUnconfirmed,
}

// A transition can publish several ordered value changes before completion.
// Payloadless phases describe the aggregate editor, not a particular effect.
internal readonly record struct LightingEditorChange(
    LightingEditorChangeKind Kind,
    LightingEditorValue? Value = null);

// Owns keyboard/logo values and their transformations only. Synchronous changes
// let the facade preserve notification order and intermediate draft observations;
// this component never calls the service or chooses whether a write is admitted.
internal sealed class LightingEditorState
{
    private const byte DefaultBrightness = 50;
    private static readonly RgbColor DefaultDualSecondaryColor = new(255, 0, 170);
    private static readonly RgbColor DefaultTidalSecondaryColor = new(0, 0, 255);

    public event Action<LightingEditorChange>? Changed;

    public bool IsDirty => lightingEditorDirty;

    public bool IsEffectUnconfirmed => keyboardEffectUnconfirmed;

    public bool UsesPrimaryColor => EffectUsesPrimaryColor(Effect);

    private LightingEffect lightingEffectEditor = LightingEffect.Off;
    private bool keyboardEffectUnconfirmed;
    private int lightingBrightnessEditor;
    private int lightingRedEditor;
    private int lightingGreenEditor;
    private int lightingBlueEditor;
    private int lightingSecondaryRedEditor;
    private int lightingSecondaryGreenEditor;
    private int lightingSecondaryBlueEditor;
    private BreathingColorMode lightingBreathingColorModeEditor = BreathingColorMode.Single;
    private StarlightColorMode lightingStarlightColorModeEditor = StarlightColorMode.Single;
    private StarlightLightingDuration lightingStarlightDurationEditor =
        StarlightLightingDuration.Medium;
    private ReactiveLightingDuration lightingReactiveDurationEditor =
        ReactiveLightingDuration.Medium;
    private SequenceLightingParameters? importedSequenceParameters;
    private bool lightingRandomColorsEditor;
    private LightingDirection lightingDirectionEditor = LightingDirection.Rightward;
    private LightingRotationDirection lightingRotationDirectionEditor =
        LightingRotationDirection.Clockwise;
    private AmbientAwarenessRegion lightingAmbientAwarenessRegionEditor =
        AmbientAwarenessRegion.Full;
    private double lightingAudioMeterColorBoostEditor = 1;
    private int lightingAudioMeterDecayMillisecondsEditor =
        LightingSettingsFactory.DefaultAudioMeterDecayMilliseconds;
    private LogoLightingMode? logoLightingModeEditor;
    private bool lightingEditorDirty;
    private PowerSource? lastConfirmedLightingEditorPowerSource;

    public LightingEffect Effect
    {
        get => lightingEffectEditor;
        set
        {
            LightingEffect previous = lightingEffectEditor;
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetEffectUnconfirmed(false);
            if (!SetLightingEditorField(ref lightingEffectEditor, value, LightingEditorValue.Effect))
            {
                return;
            }

            SeedVisibleLightingDefaults(previous, value);

            Publish(LightingEditorChangeKind.ParametersChanged);
            Publish(LightingEditorChangeKind.TransitionCompleted, LightingEditorValue.Effect);
        }
    }

    public int Brightness
    {
        get => lightingBrightnessEditor;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            SetLightingEditorField(ref lightingBrightnessEditor, value, LightingEditorValue.Brightness);
        }
    }

    public int Red
    {
        get => lightingRedEditor;
        set
        {
            if (SetLightingColorField(ref lightingRedEditor, value, LightingEditorValue.Red))
            {
                Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.PrimaryColor);
            }
        }
    }

    public int Green
    {
        get => lightingGreenEditor;
        set
        {
            if (SetLightingColorField(ref lightingGreenEditor, value, LightingEditorValue.Green))
            {
                Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.PrimaryColor);
            }
        }
    }

    public int Blue
    {
        get => lightingBlueEditor;
        set
        {
            if (SetLightingColorField(ref lightingBlueEditor, value, LightingEditorValue.Blue))
            {
                Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.PrimaryColor);
            }
        }
    }

    public RgbColor PrimaryColor
    {
        get => new(
            (byte)Red,
            (byte)Green,
            (byte)Blue);
        set => SetLightingColorEditor(value, secondary: false);
    }

    public int SecondaryRed
    {
        get => lightingSecondaryRedEditor;
        set
        {
            if (SetLightingColorField(ref lightingSecondaryRedEditor, value, LightingEditorValue.SecondaryRed))
            {
                Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.SecondaryColor);
            }
        }
    }

    public int SecondaryGreen
    {
        get => lightingSecondaryGreenEditor;
        set
        {
            if (SetLightingColorField(ref lightingSecondaryGreenEditor, value, LightingEditorValue.SecondaryGreen))
            {
                Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.SecondaryColor);
            }
        }
    }

    public int SecondaryBlue
    {
        get => lightingSecondaryBlueEditor;
        set
        {
            if (SetLightingColorField(ref lightingSecondaryBlueEditor, value, LightingEditorValue.SecondaryBlue))
            {
                Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.SecondaryColor);
            }
        }
    }

    public RgbColor SecondaryColor
    {
        get => new(
            (byte)SecondaryRed,
            (byte)SecondaryGreen,
            (byte)SecondaryBlue);
        set => SetLightingColorEditor(value, secondary: true);
    }

    public BreathingColorMode BreathingColorMode
    {
        get => lightingBreathingColorModeEditor;
        set
        {
            BreathingColorMode previous = lightingBreathingColorModeEditor;
            if (!Enum.IsDefined(value)
                || !SetLightingEditorField(ref lightingBreathingColorModeEditor, value, LightingEditorValue.BreathingColorMode))
            {
                return;
            }

            SeedBreathingColorDefaults(value, seedPrimary: previous == BreathingColorMode.Random);

            Publish(LightingEditorChangeKind.ParametersChanged);
        }
    }

    public ReactiveLightingDuration ReactiveDuration
    {
        get => lightingReactiveDurationEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetLightingEditorField(ref lightingReactiveDurationEditor, value, LightingEditorValue.ReactiveDuration);
        }
    }

    public StarlightColorMode StarlightColorMode
    {
        get => lightingStarlightColorModeEditor;
        set
        {
            StarlightColorMode previous = lightingStarlightColorModeEditor;
            if (!Enum.IsDefined(value)
                || !SetLightingEditorField(ref lightingStarlightColorModeEditor, value, LightingEditorValue.StarlightColorMode))
            {
                return;
            }

            SeedStarlightColorDefaults(value, seedPrimary: previous == StarlightColorMode.Random);
            Publish(LightingEditorChangeKind.ParametersChanged);
        }
    }

    public StarlightLightingDuration StarlightDuration
    {
        get => lightingStarlightDurationEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetLightingEditorField(ref lightingStarlightDurationEditor, value, LightingEditorValue.StarlightDuration);
        }
    }

    public bool RandomColors
    {
        get => lightingRandomColorsEditor;
        set
        {
            if (!SetLightingEditorField(ref lightingRandomColorsEditor, value, LightingEditorValue.RandomColors))
            {
                return;
            }

            Publish(LightingEditorChangeKind.ParametersChanged);
        }
    }

    public LightingDirection Direction
    {
        get => lightingDirectionEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            if (SetLightingEditorField(ref lightingDirectionEditor, value, LightingEditorValue.Direction))
            {
                Publish(LightingEditorChangeKind.TransitionCompleted, LightingEditorValue.Direction);
            }
        }
    }

    public LightingRotationDirection RotationDirection
    {
        get => lightingRotationDirectionEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetLightingEditorField(ref lightingRotationDirectionEditor, value, LightingEditorValue.RotationDirection);
        }
    }

    public AmbientAwarenessRegion AmbientAwarenessRegion
    {
        get => lightingAmbientAwarenessRegionEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            if (SetLightingEditorField(ref lightingAmbientAwarenessRegionEditor, value, LightingEditorValue.AmbientAwarenessRegion))
            {
                Publish(LightingEditorChangeKind.TransitionCompleted, LightingEditorValue.AmbientAwarenessRegion);
            }
        }
    }

    public double AudioMeterColorBoost
    {
        get => lightingAudioMeterColorBoostEditor;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 4);
            if (!double.IsFinite(value)
                || value * 4 != Math.Truncate(value * 4))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            SetLightingEditorField(ref lightingAudioMeterColorBoostEditor, value, LightingEditorValue.AudioMeterColorBoost);
        }
    }

    public int AudioMeterDecayMilliseconds
    {
        get => lightingAudioMeterDecayMillisecondsEditor;
        set
        {
            if (value is
                    < LightingSettingsFactory.MinAudioMeterDecayMilliseconds
                    or > LightingSettingsFactory.MaxAudioMeterDecayMilliseconds
                || value % LightingSettingsFactory.AudioMeterDecayStepMilliseconds != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (SetLightingEditorField(
                ref lightingAudioMeterDecayMillisecondsEditor,
                value, LightingEditorValue.AudioMeterDecayMilliseconds))
            {
                Publish(LightingEditorChangeKind.TransitionCompleted, LightingEditorValue.AudioMeterDecayMilliseconds);
            }
        }
    }

    public LogoLightingMode? LogoMode
    {
        get => logoLightingModeEditor;
        set
        {
            if (value is { } mode && !Enum.IsDefined(mode))
            {
                return;
            }

            if (SetLightingEditorField(ref logoLightingModeEditor, value, LightingEditorValue.LogoMode))
            {
                Publish(LightingEditorChangeKind.TransitionCompleted, LightingEditorValue.LogoMode);
            }
        }
    }

    public void Load(
        LightingSettings value,
        bool startUnconfirmedEffectAtDefaultBrightness = false)
    {
        if (value.Parameters is SequenceLightingParameters sequence)
        {
            importedSequenceParameters = sequence;
        }
        int brightness = value.Effect == LightingEffect.Off
            ? 0
            : value.Brightness == 0
                ? DefaultBrightness
                : value.Brightness;
        if (startUnconfirmedEffectAtDefaultBrightness
            && keyboardEffectUnconfirmed
            && brightness == 0)
        {
            // PID 02E0 reports brightness independently but cannot report
            // its current matrix effect. Zero in this state means unknown,
            // not that the lit keyboard is actually at zero percent. Start
            // a newly selected visible effect at a neutral, visible level.
            brightness = (int)DefaultBrightness;
        }
        SetValue(ref lightingEffectEditor, value.Effect, LightingEditorValue.Effect);
        SetValue(ref lightingBrightnessEditor, brightness, LightingEditorValue.Brightness);
        // Random Tidal retains its editable color in typed parameters while
        // the top-level Color is black. Preserve it for round trips and toggling
        // random colors off; other effects keep their existing color decoding.
        RgbColor primaryColor = value.Effect == LightingEffect.Tidal
            && value.Parameters is TidalLightingParameters tidalPrimary
                ? tidalPrimary.PrimaryColor
                : value.Color;
        SetValue(ref lightingRedEditor, (int)primaryColor.Red, LightingEditorValue.Red);
        SetValue(ref lightingGreenEditor, (int)primaryColor.Green, LightingEditorValue.Green);
        SetValue(ref lightingBlueEditor, (int)primaryColor.Blue, LightingEditorValue.Blue);
        RgbColor secondaryColor = value.Parameters switch
        {
            BreathingLightingParameters { SecondaryColor: { } color } => color,
            TidalLightingParameters tidal => tidal.SecondaryColor,
            StarlightLightingParameters { SecondaryColor: { } color } => color,
            _ => value.Color,
        };
        SetValue(
            ref lightingSecondaryRedEditor,
            (int)secondaryColor.Red,
            LightingEditorValue.SecondaryRed);
        SetValue(
            ref lightingSecondaryGreenEditor,
            (int)secondaryColor.Green,
            LightingEditorValue.SecondaryGreen);
        SetValue(
            ref lightingSecondaryBlueEditor,
            (int)secondaryColor.Blue,
            LightingEditorValue.SecondaryBlue);
        Publish(LightingEditorChangeKind.Refreshed, LightingEditorValue.PrimaryColor);
        Publish(LightingEditorChangeKind.Refreshed, LightingEditorValue.SecondaryColor);
        BreathingColorMode breathingColorMode =
            value.Parameters is BreathingLightingParameters breathing
                ? breathing.ColorMode
                : BreathingColorMode.Single;
        SetValue(
            ref lightingBreathingColorModeEditor,
            breathingColorMode,
            LightingEditorValue.BreathingColorMode);
        ReactiveLightingDuration reactiveDuration =
            value.Parameters is ReactiveLightingParameters reactive
                ? reactive.Duration
                : ReactiveLightingDuration.Medium;
        SetValue(
            ref lightingReactiveDurationEditor,
            reactiveDuration,
            LightingEditorValue.ReactiveDuration);
        StarlightColorMode starlightColorMode =
            value.Parameters is StarlightLightingParameters starlight
                ? starlight.ColorMode
                : StarlightColorMode.Single;
        SetValue(
            ref lightingStarlightColorModeEditor,
            starlightColorMode,
            LightingEditorValue.StarlightColorMode);
        StarlightLightingDuration starlightDuration =
            value.Parameters is StarlightLightingParameters starlightParameters
                ? starlightParameters.Duration
                : StarlightLightingDuration.Medium;
        SetValue(
            ref lightingStarlightDurationEditor,
            starlightDuration,
            LightingEditorValue.StarlightDuration);
        bool randomColors = value.Parameters is TidalLightingParameters
        {
            RandomColors: true,
        };
        SetValue(
            ref lightingRandomColorsEditor,
            randomColors,
            LightingEditorValue.RandomColors);
        LightingDirection direction = value.Parameters switch
        {
            TidalLightingParameters tidalParameters => tidalParameters.Direction,
            WaveLightingParameters waveParameters => waveParameters.Direction,
            _ => LightingDirection.Rightward,
        };
        SetValue(
            ref lightingDirectionEditor,
            direction,
            LightingEditorValue.Direction);
        LightingRotationDirection rotationDirection =
            value.Parameters is WheelLightingParameters wheelParameters
                ? wheelParameters.Direction
                : LightingRotationDirection.Clockwise;
        SetValue(
            ref lightingRotationDirectionEditor,
            rotationDirection,
            LightingEditorValue.RotationDirection);
        AmbientAwarenessRegion ambientAwarenessRegion =
            value.Parameters is AmbientAwarenessLightingParameters ambientAwareness
                ? ambientAwareness.Region
                : AmbientAwarenessRegion.Full;
        SetValue(
            ref lightingAmbientAwarenessRegionEditor,
            ambientAwarenessRegion,
            LightingEditorValue.AmbientAwarenessRegion);
        double audioMeterColorBoost =
            value.Parameters is AudioMeterLightingParameters audioMeter
                ? audioMeter.ColorBoost
                : 1;
        SetValue(
            ref lightingAudioMeterColorBoostEditor,
            audioMeterColorBoost,
            LightingEditorValue.AudioMeterColorBoost);
        int audioMeterDecayMilliseconds =
            value.Parameters is AudioMeterLightingParameters audioMeterDecay
                ? audioMeterDecay.DecayMilliseconds
                : LightingSettingsFactory.DefaultAudioMeterDecayMilliseconds;
        SetValue(
            ref lightingAudioMeterDecayMillisecondsEditor,
            audioMeterDecayMilliseconds,
            LightingEditorValue.AudioMeterDecayMilliseconds);
        Publish(LightingEditorChangeKind.Refreshed, LightingEditorValue.AudioMeterDecayMilliseconds);
        SetValue(
            ref logoLightingModeEditor,
            value.LogoMode,
            LightingEditorValue.LogoMode);
    }

    // Explicitly choosing the hidden desired effect seeds defaults and marks
    // an edit even when its enum value did not change.
    public void SelectEffect(LightingEffect effect)
    {
        bool wasUnconfirmed = IsEffectUnconfirmed;
        SetEffectUnconfirmed(false);
        if (wasUnconfirmed && Effect == effect)
        {
            SeedVisibleLightingDefaults(Effect, effect);
            SetDirty(true);
            Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.Draft);
            Publish(LightingEditorChangeKind.ParametersChanged);
            Publish(LightingEditorChangeKind.EditCompleted);
            return;
        }
        Effect = effect;
    }

    public LightingSettings ToSettings()
    {
        byte brightness = Effect != LightingEffect.Off
            && Brightness == 0
                ? DefaultBrightness
                : (byte)Brightness;
        RgbColor primaryColor = new(
            (byte)Red,
            (byte)Green,
            (byte)Blue);
        RgbColor secondaryColor = new(
            (byte)SecondaryRed,
            (byte)SecondaryGreen,
            (byte)SecondaryBlue);
        LightingSettings keyboard = Effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Static => LightingSettingsFactory.Static(primaryColor, brightness),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(brightness),
            LightingEffect.Breathing when
                BreathingColorMode == BreathingColorMode.Single =>
                LightingSettingsFactory.BreathingSingle(primaryColor, brightness),
            LightingEffect.Breathing when
                BreathingColorMode == BreathingColorMode.Dual =>
                LightingSettingsFactory.BreathingDual(
                    primaryColor,
                    secondaryColor,
                    brightness),
            LightingEffect.Breathing when
                BreathingColorMode == BreathingColorMode.Random =>
                LightingSettingsFactory.BreathingRandom(brightness),
            LightingEffect.Reactive => LightingSettingsFactory.Reactive(
                primaryColor,
                brightness,
                ReactiveDuration),
            LightingEffect.Ripple => LightingSettingsFactory.Ripple(primaryColor, brightness),
            LightingEffect.Tidal => LightingSettingsFactory.Tidal(
                primaryColor,
                secondaryColor,
                RandomColors,
                Direction,
                brightness),
            LightingEffect.Starlight when
                StarlightColorMode == StarlightColorMode.Single =>
                LightingSettingsFactory.StarlightSingle(
                    primaryColor,
                    brightness,
                    StarlightDuration),
            LightingEffect.Starlight when
                StarlightColorMode == StarlightColorMode.Dual =>
                LightingSettingsFactory.StarlightDual(
                    primaryColor,
                    secondaryColor,
                    brightness,
                    StarlightDuration),
            LightingEffect.Starlight when
                StarlightColorMode == StarlightColorMode.Random =>
                LightingSettingsFactory.StarlightRandom(
                    brightness,
                    StarlightDuration),
            LightingEffect.Wave => LightingSettingsFactory.Wave(
                Direction,
                brightness),
            LightingEffect.Wheel => LightingSettingsFactory.Wheel(
                RotationDirection,
                brightness),
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion,
                brightness),
            LightingEffect.AudioMeter => LightingSettingsFactory.AudioMeter(
                AudioMeterColorBoost,
                brightness,
                AudioMeterDecayMilliseconds),
            LightingEffect.Fire => LightingSettingsFactory.Fire(brightness),
            LightingEffect.Sequence when importedSequenceParameters is not null =>
                LightingSettingsFactory.Sequence(
                    importedSequenceParameters.LayoutId,
                    importedSequenceParameters.TimelineBase64,
                    importedSequenceParameters.Loop,
                    brightness),
            _ => new LightingSettings(Effect, default, brightness),
        };
        return keyboard with { LogoMode = LogoMode };
    }

    private void SeedVisibleLightingDefaults(
        LightingEffect previous,
        LightingEffect selected)
    {
        if (selected == LightingEffect.Off)
        {
            return;
        }

        if (Brightness == 0)
        {
            SetLightingEditorField(
                ref lightingBrightnessEditor,
                (int)DefaultBrightness,
                LightingEditorValue.Brightness);
        }

        bool selectedUsesColor = selected is LightingEffect.Static
            or LightingEffect.Breathing
            or LightingEffect.Reactive
            or LightingEffect.Ripple
            or LightingEffect.Tidal
            or LightingEffect.Starlight;
        bool previousHadNoEditableColor = !EffectUsesPrimaryColor(previous);
        if (selectedUsesColor
            && previousHadNoEditableColor
            && PrimaryColor == default)
        {
            SetLightingColorEditor(
                LightingSettingsFactory.DefaultVisibleColor,
                secondary: false);
        }

        // The source-aware decision above owns primary seeding for effect
        // changes. Keep secondary defaults and direct mode-change seeding.
        if (selected == LightingEffect.Breathing)
        {
            SeedBreathingColorDefaults(BreathingColorMode, seedPrimary: false);
        }

        if (selected == LightingEffect.Tidal)
        {
            SeedTidalSecondaryColorDefault();
        }

        if (selected == LightingEffect.Starlight)
        {
            SeedStarlightColorDefaults(StarlightColorMode, seedPrimary: false);
        }
    }

    // Share the source-color decision with the editor's color availability.
    // A black color from an editable source is intentional; a colorless source
    // has no such intent unless it retained a nonblack value while editing.
    private bool EffectUsesPrimaryColor(LightingEffect effect) =>
        effect is LightingEffect.Static or LightingEffect.Reactive or LightingEffect.Ripple
        || effect == LightingEffect.Breathing && BreathingColorMode != BreathingColorMode.Random
        || effect == LightingEffect.Tidal && !RandomColors
        || effect == LightingEffect.Starlight && StarlightColorMode != StarlightColorMode.Random;

    private void SeedBreathingColorDefaults(BreathingColorMode selected, bool seedPrimary)
    {
        if (seedPrimary && selected is BreathingColorMode.Single or BreathingColorMode.Dual
            && PrimaryColor == default)
        {
            SetLightingColorEditor(
                LightingSettingsFactory.DefaultVisibleColor,
                secondary: false);
        }

        if (selected == BreathingColorMode.Dual
            && SecondaryColor == default)
        {
            SetLightingColorEditor(DefaultDualSecondaryColor, secondary: true);
        }
    }

    private void SeedTidalSecondaryColorDefault()
    {
        if (SecondaryColor == default)
        {
            SetLightingColorEditor(DefaultTidalSecondaryColor, secondary: true);
        }
    }

    private void SeedStarlightColorDefaults(StarlightColorMode selected, bool seedPrimary)
    {
        if (seedPrimary && selected is StarlightColorMode.Single or StarlightColorMode.Dual
            && PrimaryColor == default)
        {
            SetLightingColorEditor(
                LightingSettingsFactory.DefaultVisibleColor,
                secondary: false);
        }

        if (selected == StarlightColorMode.Dual
            && SecondaryColor == default)
        {
            SetLightingColorEditor(DefaultDualSecondaryColor, secondary: true);
        }
    }

    private bool SetLightingColorField(
        ref int field,
        int value,
        LightingEditorValue fieldName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
        return SetLightingEditorField(ref field, value, fieldName);
    }

    private void SetLightingColorEditor(RgbColor value, bool secondary)
    {
        ref int red = ref (secondary
            ? ref lightingSecondaryRedEditor
            : ref lightingRedEditor);
        ref int green = ref (secondary
            ? ref lightingSecondaryGreenEditor
            : ref lightingGreenEditor);
        ref int blue = ref (secondary
            ? ref lightingSecondaryBlueEditor
            : ref lightingBlueEditor);
        if (red == value.Red && green == value.Green && blue == value.Blue)
        {
            return;
        }

        SetDirty(true);
        SetValue(ref red, (int)value.Red, secondary ? LightingEditorValue.SecondaryRed : LightingEditorValue.Red);
        SetValue(ref green, (int)value.Green, secondary ? LightingEditorValue.SecondaryGreen : LightingEditorValue.Green);
        SetValue(ref blue, (int)value.Blue, secondary ? LightingEditorValue.SecondaryBlue : LightingEditorValue.Blue);
        Publish(LightingEditorChangeKind.ValueChanged, secondary
            ? LightingEditorValue.SecondaryColor
            : LightingEditorValue.PrimaryColor);
        Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.Draft);
        Publish(LightingEditorChangeKind.EditCompleted);
    }

    private bool SetLightingEditorField<T>(
        ref T field,
        T value,
        LightingEditorValue fieldName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        SetDirty(true);
        SetValue(ref field, value, fieldName);
        Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.Draft);
        Publish(LightingEditorChangeKind.EditCompleted);
        return true;
    }

    public bool ClearDirtyIfUnchanged(LightingSettings submitted)
    {
        if (ToSettings() != submitted)
        {
            return false;
        }
        SetDirty(false);
        return true;
    }

    public void SetDirty(bool value)
    {
        if (lightingEditorDirty == value)
        {
            return;
        }

        lightingEditorDirty = value;
        Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.Dirty);
    }

    public void SetEffectUnconfirmed(bool value)
    {
        if (keyboardEffectUnconfirmed == value)
        {
            return;
        }

        keyboardEffectUnconfirmed = value;
        Publish(LightingEditorChangeKind.ValueChanged, LightingEditorValue.EffectUnconfirmed);
    }

    public void PrepareForPowerSource(PowerSource powerSource)
    {
        if (powerSource is not (PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return;
        }

        if (lastConfirmedLightingEditorPowerSource is { } previousPowerSource
            && previousPowerSource != powerSource)
        {
            SetDirty(false);
        }

        lastConfirmedLightingEditorPowerSource = powerSource;
    }

    public static LightingSettings CreateChoice(LightingEffect effect, LightingSettings selected)
    {
        byte brightness = selected.Brightness == 0
            ? DefaultBrightness
            : selected.Brightness;
        RgbColor color = selected.Color == default
            ? LightingSettingsFactory.DefaultVisibleColor
            : selected.Color;
        return effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Static => LightingSettingsFactory.Static(color, brightness),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(brightness),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingSingle(
                color,
                brightness),
            LightingEffect.Reactive => LightingSettingsFactory.Reactive(
                color,
                brightness,
                ReactiveLightingDuration.Medium),
            LightingEffect.Ripple => LightingSettingsFactory.Ripple(color, brightness),
            LightingEffect.Tidal => LightingSettingsFactory.Tidal(
                color,
                DefaultTidalSecondaryColor,
                randomColors: false,
                LightingDirection.Rightward,
                brightness),
            LightingEffect.Starlight => LightingSettingsFactory.StarlightSingle(
                color,
                brightness,
                StarlightLightingDuration.Medium),
            LightingEffect.Wave => LightingSettingsFactory.Wave(
                LightingDirection.Rightward,
                brightness),
            LightingEffect.Wheel => LightingSettingsFactory.Wheel(
                LightingRotationDirection.Clockwise,
                brightness),
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Full,
                brightness),
            LightingEffect.AudioMeter => LightingSettingsFactory.AudioMeter(
                colorBoost: 1,
                brightness),
            LightingEffect.Fire => LightingSettingsFactory.Fire(brightness),
            _ => new LightingSettings(effect, color, brightness),
        };
    }

    private void SetValue<T>(ref T field, T value, LightingEditorValue fieldName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        Publish(LightingEditorChangeKind.ValueChanged, fieldName);
    }

    private void Publish(LightingEditorChangeKind kind, LightingEditorValue? value = null) =>
        Changed?.Invoke(new LightingEditorChange(kind, value));
}
