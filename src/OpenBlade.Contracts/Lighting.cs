using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public enum BreathingColorMode
{
    Single,
    Dual,
    Random,
}

public enum ReactiveLightingDuration
{
    Short,
    Medium,
    Long,
}

public enum StarlightColorMode
{
    Single,
    Dual,
    Random,
}

public enum StarlightLightingDuration
{
    Short,
    Medium,
    Long,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OffLightingParameters), "off")]
[JsonDerivedType(typeof(StaticLightingParameters), "static")]
[JsonDerivedType(typeof(SpectrumCyclingLightingParameters), "spectrumCycling")]
[JsonDerivedType(typeof(BreathingLightingParameters), "breathing")]
[JsonDerivedType(typeof(ReactiveLightingParameters), "reactive")]
[JsonDerivedType(typeof(TidalLightingParameters), "tidal")]
[JsonDerivedType(typeof(StarlightLightingParameters), "starlight")]
[JsonDerivedType(typeof(WaveLightingParameters), "wave")]
[JsonDerivedType(typeof(WheelLightingParameters), "wheel")]
[JsonDerivedType(typeof(RippleLightingParameters), "ripple")]
[JsonDerivedType(typeof(AmbientAwarenessLightingParameters), "ambientAwareness")]
public abstract record LightingEffectParameters;

public sealed record OffLightingParameters : LightingEffectParameters;

public sealed record StaticLightingParameters(RgbColor Color)
    : LightingEffectParameters;

public sealed record SpectrumCyclingLightingParameters : LightingEffectParameters;

public sealed record BreathingLightingParameters(
    BreathingColorMode ColorMode,
    RgbColor? PrimaryColor,
    RgbColor? SecondaryColor)
    : LightingEffectParameters;

public sealed record ReactiveLightingParameters(
    RgbColor Color,
    ReactiveLightingDuration Duration)
    : LightingEffectParameters;

public sealed record TidalLightingParameters(
    RgbColor PrimaryColor,
    RgbColor SecondaryColor,
    bool RandomColors,
    LightingDirection Direction)
    : LightingEffectParameters;

public sealed record StarlightLightingParameters(
    StarlightColorMode ColorMode,
    RgbColor? PrimaryColor,
    RgbColor? SecondaryColor,
    StarlightLightingDuration Duration)
    : LightingEffectParameters;

public sealed record WaveLightingParameters(LightingDirection Direction)
    : LightingEffectParameters;

public sealed record WheelLightingParameters(LightingRotationDirection Direction)
    : LightingEffectParameters;

public sealed record RippleLightingParameters(RgbColor Color)
    : LightingEffectParameters;

public sealed record AmbientAwarenessLightingParameters(AmbientAwarenessRegion Region)
    : LightingEffectParameters;

public static class LightingSettingsFactory
{
    public const byte DefaultVisibleBrightness = 100;
    public static RgbColor DefaultVisibleColor { get; } = new(0x47, 0xE1, 0x0C);

    public static LightingSettings Off() => new(
        LightingEffect.Off,
        default,
        Brightness: 0)
    {
        Parameters = new OffLightingParameters(),
    };

    public static LightingSettings Static(RgbColor color, byte brightness) => new(
        LightingEffect.Static,
        color,
        brightness)
    {
        Parameters = new StaticLightingParameters(color),
    };

    public static LightingSettings SpectrumCycling(byte brightness) => new(
        LightingEffect.Spectrum,
        default,
        brightness)
    {
        Parameters = new SpectrumCyclingLightingParameters(),
    };

    public static LightingSettings BreathingSingle(
        RgbColor color,
        byte brightness) => new(
            LightingEffect.Breathing,
            color,
            brightness)
        {
            Parameters = new BreathingLightingParameters(
                BreathingColorMode.Single,
                color,
                SecondaryColor: null),
        };

    public static LightingSettings BreathingDual(
        RgbColor primaryColor,
        RgbColor secondaryColor,
        byte brightness) => new(
            LightingEffect.Breathing,
            primaryColor,
            brightness)
        {
            Parameters = new BreathingLightingParameters(
                BreathingColorMode.Dual,
                primaryColor,
                secondaryColor),
        };

    public static LightingSettings BreathingRandom(byte brightness) => new(
        LightingEffect.Breathing,
        default,
        brightness)
    {
        Parameters = new BreathingLightingParameters(
            BreathingColorMode.Random,
            PrimaryColor: null,
            SecondaryColor: null),
    };

    public static LightingSettings Reactive(
        RgbColor color,
        byte brightness,
        ReactiveLightingDuration duration) => new(
            LightingEffect.Reactive,
            color,
            brightness)
        {
            Parameters = new ReactiveLightingParameters(color, duration),
        };

    public static LightingSettings Tidal(
        RgbColor primaryColor,
        RgbColor secondaryColor,
        bool randomColors,
        LightingDirection direction,
        byte brightness) => new(
            LightingEffect.Tidal,
            randomColors ? default : primaryColor,
            brightness)
        {
            Parameters = new TidalLightingParameters(
                primaryColor,
                secondaryColor,
                randomColors,
                direction),
        };

    public static LightingSettings StarlightSingle(
        RgbColor color,
        byte brightness,
        StarlightLightingDuration duration) => new(
            LightingEffect.Starlight,
            color,
            brightness)
        {
            Parameters = new StarlightLightingParameters(
                StarlightColorMode.Single,
                color,
                SecondaryColor: null,
                duration),
        };

    public static LightingSettings StarlightDual(
        RgbColor primaryColor,
        RgbColor secondaryColor,
        byte brightness,
        StarlightLightingDuration duration) => new(
            LightingEffect.Starlight,
            primaryColor,
            brightness)
        {
            Parameters = new StarlightLightingParameters(
                StarlightColorMode.Dual,
                primaryColor,
                secondaryColor,
                duration),
        };

    public static LightingSettings StarlightRandom(
        byte brightness,
        StarlightLightingDuration duration) => new(
            LightingEffect.Starlight,
            default,
            brightness)
        {
            Parameters = new StarlightLightingParameters(
                StarlightColorMode.Random,
                PrimaryColor: null,
                SecondaryColor: null,
                duration),
        };

    public static LightingSettings Wave(
        LightingDirection direction,
        byte brightness) => new(
            LightingEffect.Wave,
            default,
            brightness)
        {
            Parameters = new WaveLightingParameters(direction),
        };

    public static LightingSettings Wheel(
        LightingRotationDirection direction,
        byte brightness) => new(
            LightingEffect.Wheel,
            default,
            brightness)
        {
            Parameters = new WheelLightingParameters(direction),
        };

    public static LightingSettings Ripple(RgbColor color, byte brightness) => new(
        LightingEffect.Ripple,
        color,
        brightness)
    {
        Parameters = new RippleLightingParameters(color),
    };

    public static LightingSettings AmbientAwareness(
        AmbientAwarenessRegion region,
        byte brightness) => new(
            LightingEffect.AmbientAwareness,
            default,
            brightness)
        {
            Parameters = new AmbientAwarenessLightingParameters(region),
        };

    public static LightingSettings NormalizeLegacyOff(LightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Parameters is null
            && settings.Effect == LightingEffect.Off
            && settings.Color == default
            && settings.Brightness == 0
                ? Off()
                : settings;
    }

    public static LightingSettings NormalizeLegacyInvisibleEffect(
        LightingSettings settings)
    {
        settings = NormalizeLegacyOff(settings);
        if (settings.Effect == LightingEffect.Off || settings.Brightness != 0)
        {
            return settings;
        }

        RgbColor visibleColor = settings.Color == default
            ? DefaultVisibleColor
            : settings.Color;
        return settings.Parameters switch
        {
            StaticLightingParameters => Static(visibleColor, DefaultVisibleBrightness),
            SpectrumCyclingLightingParameters => SpectrumCycling(DefaultVisibleBrightness),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Single,
            } => BreathingSingle(visibleColor, DefaultVisibleBrightness),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Dual,
                SecondaryColor: { } secondary,
            } => BreathingDual(
                visibleColor,
                secondary == default ? new RgbColor(255, 0, 170) : secondary,
                DefaultVisibleBrightness),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Random,
            } => BreathingRandom(DefaultVisibleBrightness),
            ReactiveLightingParameters reactive => Reactive(
                visibleColor,
                DefaultVisibleBrightness,
                reactive.Duration),
            TidalLightingParameters tidal => Tidal(
                tidal.PrimaryColor == default ? DefaultVisibleColor : tidal.PrimaryColor,
                tidal.SecondaryColor == default
                    ? new RgbColor(255, 0, 170)
                    : tidal.SecondaryColor,
                tidal.RandomColors,
                tidal.Direction,
                DefaultVisibleBrightness),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Single,
            } starlight => StarlightSingle(
                visibleColor,
                DefaultVisibleBrightness,
                starlight.Duration),
            WaveLightingParameters wave => Wave(
                wave.Direction,
                DefaultVisibleBrightness),
            WheelLightingParameters wheel => Wheel(
                wheel.Direction,
                DefaultVisibleBrightness),
            RippleLightingParameters => Ripple(
                visibleColor,
                DefaultVisibleBrightness),
            AmbientAwarenessLightingParameters ambient => AmbientAwareness(
                ambient.Region,
                DefaultVisibleBrightness),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
                SecondaryColor: { } secondary,
            } starlight => StarlightDual(
                visibleColor,
                secondary == default ? new RgbColor(255, 0, 170) : secondary,
                DefaultVisibleBrightness,
                starlight.Duration),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Random,
            } starlight => StarlightRandom(
                DefaultVisibleBrightness,
                starlight.Duration),
            null => settings.Effect switch
            {
                LightingEffect.Static => Static(visibleColor, DefaultVisibleBrightness),
                LightingEffect.Spectrum => SpectrumCycling(DefaultVisibleBrightness),
                LightingEffect.Breathing => BreathingSingle(
                    visibleColor,
                    DefaultVisibleBrightness),
                LightingEffect.Reactive => Reactive(
                    visibleColor,
                    DefaultVisibleBrightness,
                    ReactiveLightingDuration.Medium),
                _ => settings,
            },
            _ => settings,
        };
    }
}

public static class LightingSettingsValidation
{
    public static bool IsWellFormed(LightingSettings? settings)
    {
        if (settings is null
            || !Enum.IsDefined(settings.Effect)
            || settings.LogoMode is { } logoMode && !Enum.IsDefined(logoMode)
            || settings.Brightness > 100
            || (settings.Effect != LightingEffect.Off && settings.Brightness == 0))
        {
            return false;
        }

        return settings.Parameters is null
            ? IsLegacyShapeWellFormed(settings)
            : IsTypedShapeWellFormed(settings);
    }

    private static bool IsLegacyShapeWellFormed(LightingSettings settings) =>
        settings.Effect switch
        {
            LightingEffect.Off => settings.Brightness == 0 && settings.Color == default,
            LightingEffect.Spectrum => settings.Color == default,
            LightingEffect.Static or LightingEffect.Breathing or LightingEffect.Reactive => true,
            _ => false,
        };

    private static bool IsTypedShapeWellFormed(LightingSettings settings) =>
        (settings.Effect, settings.Parameters) switch
        {
            (LightingEffect.Off, OffLightingParameters) =>
                settings.Brightness == 0 && settings.Color == default,
            (LightingEffect.Static, StaticLightingParameters parameters) =>
                settings.Color == parameters.Color,
            (LightingEffect.Spectrum, SpectrumCyclingLightingParameters) =>
                settings.Color == default,
            (LightingEffect.Breathing, BreathingLightingParameters parameters) =>
                ValidBreathing(settings, parameters),
            (LightingEffect.Reactive, ReactiveLightingParameters parameters) =>
                Enum.IsDefined(parameters.Duration)
                && settings.Color == parameters.Color,
            (LightingEffect.Tidal, TidalLightingParameters parameters) =>
                Enum.IsDefined(parameters.Direction)
                && settings.Color == (parameters.RandomColors
                    ? default
                    : parameters.PrimaryColor),
            (LightingEffect.Starlight, StarlightLightingParameters parameters) =>
                ValidStarlight(settings, parameters),
            (LightingEffect.Wave, WaveLightingParameters parameters) =>
                Enum.IsDefined(parameters.Direction) && settings.Color == default,
            (LightingEffect.Wheel, WheelLightingParameters parameters) =>
                Enum.IsDefined(parameters.Direction) && settings.Color == default,
            (LightingEffect.Ripple, RippleLightingParameters parameters) =>
                settings.Color == parameters.Color,
            (LightingEffect.AmbientAwareness, AmbientAwarenessLightingParameters parameters) =>
                Enum.IsDefined(parameters.Region) && settings.Color == default,
            _ => false,
        };

    private static bool ValidStarlight(
        LightingSettings settings,
        StarlightLightingParameters parameters)
    {
        if (!Enum.IsDefined(parameters.ColorMode)
            || !Enum.IsDefined(parameters.Duration))
        {
            return false;
        }

        return parameters.ColorMode switch
        {
            StarlightColorMode.Single =>
                parameters.PrimaryColor is { } primary
                && parameters.SecondaryColor is null
                && settings.Color == primary,
            StarlightColorMode.Dual =>
                parameters.PrimaryColor is { } primary
                && parameters.SecondaryColor is not null
                && settings.Color == primary,
            StarlightColorMode.Random =>
                parameters.PrimaryColor is null
                && parameters.SecondaryColor is null
                && settings.Color == default,
            _ => false,
        };
    }

    private static bool ValidBreathing(
        LightingSettings settings,
        BreathingLightingParameters parameters)
    {
        if (!Enum.IsDefined(parameters.ColorMode))
        {
            return false;
        }

        return parameters.ColorMode switch
        {
            BreathingColorMode.Single =>
                parameters.PrimaryColor is { } primary
                && parameters.SecondaryColor is null
                && settings.Color == primary,
            BreathingColorMode.Dual =>
                parameters.PrimaryColor is { } primary
                && parameters.SecondaryColor is not null
                && settings.Color == primary,
            BreathingColorMode.Random =>
                parameters.PrimaryColor is null
                && parameters.SecondaryColor is null
                && settings.Color == default,
            _ => false,
        };
    }
}
