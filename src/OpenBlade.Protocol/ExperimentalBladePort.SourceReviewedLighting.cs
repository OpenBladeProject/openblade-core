namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Each exact product's pinned Synapse LED matrix backend uses 03/83 and
    // 03/03 for brightness, separately from its unreadable 03/0A matrix effect.
    // See evidence/source-reviewed-blades/2026-11-13-lighting-coverage.json.
    internal static bool SupportsIndependentLighting(ExperimentalBladeModel model) =>
        model.ProductId is 0x0234 or 0x0245 or 0x0246 or 0x024C or 0x0253
            or 0x0268 or 0x0270 or 0x0276 or 0x02B6 || SupportsReviewedMcuLighting(model);

    // Seven separately reviewed MCU images implement a bounded active 0F/82
    // getter and matching 0F/02 setters, including actual color parameters.
    // Synapse's inherited old-matrix setter alone does not establish this API.
    internal static bool SupportsReviewedMcuLighting(ExperimentalBladeModel model) =>
        model.ProductId is 0x0245 or 0x028A or 0x028B or 0x028C or 0x02B7 or 0x02B8 or 0x02C7;

    private static bool SupportsOnlyIndependentLighting(ExperimentalBladeModel model) =>
        SupportsIndependentLighting(model) && !SupportsReviewedMcuLighting(model);

    private static bool UsesLegacyReviewedMcuEffectSetter(ExperimentalBladeModel model) =>
        model.ProductId is 0x0245 or 0x028A or 0x028B or 0x028C;

    private static bool SupportsSourceReviewedLogo(ExperimentalBladeModel model) =>
        SupportsIndependentLighting(model) && model.ProductId is not (0x0246 or 0x0268 or 0x028A);
}
