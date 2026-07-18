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

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OffLightingParameters), "off")]
[JsonDerivedType(typeof(StaticLightingParameters), "static")]
[JsonDerivedType(typeof(SpectrumCyclingLightingParameters), "spectrumCycling")]
[JsonDerivedType(typeof(BreathingLightingParameters), "breathing")]
[JsonDerivedType(typeof(ReactiveLightingParameters), "reactive")]
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

public static class LightingSettingsFactory
{
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
}

public static class LightingSettingsValidation
{
    public static bool IsWellFormed(LightingSettings? settings)
    {
        if (settings is null
            || !Enum.IsDefined(settings.Effect)
            || settings.Brightness > 100)
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
            _ => false,
        };

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
