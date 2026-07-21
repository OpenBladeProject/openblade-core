using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal static class LegacyLightingVisibilityNormalizer
{
    public const byte VisibleRecoveryBrightness = 50;

    public static LightingSettings Normalize(LightingSettings settings)
    {
        LightingSettings normalized =
            LightingSettingsFactory.NormalizeLegacyInvisibleEffect(settings);
        return settings.Effect == LightingEffect.Off
            || settings.Brightness != 0
            || normalized.Brightness == settings.Brightness
                ? normalized
                : normalized with { Brightness = VisibleRecoveryBrightness };
    }
}
