using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Reviewed exact-model sources permit controls without claiming physical validation.</summary>
internal sealed record ExperimentalBladeModel(
    string ModelNumber,
    string ProductName,
    ushort ProductId,
    bool SupportsBatteryProtection,
    string EvidenceSource,
    string? AdapterPowerEvidenceSource = null,
    SourceReviewedThermalPolicy? ThermalPolicy = null);

/// <summary>Exact-model Synapse policy; no fan range or lighting transport is inherited.</summary>
internal sealed record SourceReviewedThermalPolicy(
    string CommandEvidenceSource,
    bool SupportsMaximumFans,
    bool SupportsCpuBoost,
    bool SupportsBatterySaver,
    bool SupportsMaximumPower,
    System.Collections.Immutable.ImmutableArray<string> EvidenceSources,
    bool SupportsTurbo = true,
    bool SupportsSilent = true,
    bool SupportsBalancedDc = true,
    bool SupportsCustom = true,
    bool SupportsGaming = false,
    bool SupportsCreator = false)
{
    internal byte QueryProfile => 1;
    internal byte MaximumTransactionId => 0x1F;
}

internal static class ExperimentalBladeModels
{
    public const string RazerCtlSource = "https://github.com/tdakhran/razer-ctl/tree/8e12e778d303d4ed01eeb2c1e99ce2e97f1c8917";
    public const string BladeControlSource = "https://github.com/jdsgnrinfo/blade-control/tree/bc48c916d4d6cc47df8c0e32a73ef454dda37cdd";

    public const string Blade18Source = "https://github.com/sqmagellan/razer-ctl/tree/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a";

    internal const string Blade18SynapseSource = "https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js";
    internal const string Blade18MappingSdkSource = "https://apps.razer.com/synapse/products/672/mw/7795.8e8d6b06ca4c3fe23fe1.js";
    internal const string Blade18MappingEngineSource = "mapping_engine.dll@1.3.16.5;SHA256=6EABDFDEDF797E042738B630D827C06F7A45DBE560EBAEC96C66698F88F3320A";
    internal const string Blade18SynapseHardwareEventParserSource = "https://apps.razer.com/synapse/products/672/mw/rzHardwareEventsProtocol25Parser.b504c011548aa6f83172.js";
    internal const string Blade18ObserverSource = "https://github.com/user-attachments/files/33499731/blade-evidence-20261015-1459.zip";

    internal const string Blade18ObserverFollowupSource = "https://github.com/user-attachments/files/33530069/blade-evidence-20261016-063420.zip";

    internal const string Blade18ObserverControlsSource = "https://github.com/user-attachments/files/33595838/blade-evidence-20261017-122040.zip";

    internal const string MaintainedRazerCtlSource = "https://github.com/sqmagellan/razer-ctl/tree/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a";
    internal const string OpenRazerSource = "https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d";
    internal const string ExpandedBladeControlSource = "https://github.com/jdsgnrinfo/blade-control/tree/92beedc82e6e378cb71dac276e5820c8ea84247d";

    internal static IReadOnlyList<ExperimentalFeatureEvidence> FeatureEvidenceFor(ExperimentalBladeModel model)
    {
        if (model.ThermalPolicy is { } thermal)
        {
            IReadOnlyList<string> sources = thermal.EvidenceSources;
            return [
                new(!thermal.SupportsCustom ? "ThermalPresetsOnlyBalancedGamingCreator"
                    : thermal.SupportsCpuBoost ? "PerformanceAndCustomCpu0To3Gpu0To2" : "PerformanceAndCustomLevels0To2",
                    "SynapseSourceBackedWithoutPhysicalValidation", sources),
                .. (model.ProductId == 0x0245
                    ? new ExperimentalFeatureEvidence[] { new("CustomGpuWattageLevels0To2",
                        "SynapseSourceBackedWithoutPhysicalValidation", sources) }
                    : []),
                .. (ExperimentalBladePort.SupportsIndependentLighting(model)
                    ? new ExperimentalFeatureEvidence[] { new(model.ProductId is 0x0246 or 0x0268 or 0x028A
                        ? "IndependentKeyboardBrightness" : "IndependentKeyboardBrightnessAndLogo",
                        "SynapseSourceBackedWithoutPhysicalValidation", sources) }
                    : []),
                .. (ExperimentalBladePort.SupportsReviewedMcuLighting(model)
                    ? new ExperimentalFeatureEvidence[] { new("ActiveKeyboardLightingAndBrightness",
                        "ExactMcuAndSynapseSourceBackedWithoutPhysicalValidation",
                        [ReviewedMcuLightingSource(model), .. sources]),
                        new("KeyboardBreathingRandomSingleDualColors", "ExactMcuSourceBackedWithoutPhysicalValidation",
                            [ReviewedMcuLightingSource(model), .. sources]),
                        new("KeyboardWaveBothDirections", "ExactMcuAndSelectedModelLabelsWithoutPhysicalValidation",
                            [ReviewedMcuLightingSource(model), .. sources]),
                        new("KeyboardStaticColor", model.ProductId is 0x028A or 0x02B7
                            ? "NotEstablishedHiddenPaletteSelector" : "ExactMcuSourceBackedWithoutPhysicalValidation",
                            [ReviewedMcuLightingSource(model), .. sources]) }
                    : []),
                new("AutomaticFanHandback", "SynapseSourceBackedWithoutPhysicalValidation", sources),
                new("FixedAndCurveFanWrites", ExperimentalBladePort.UsesDynamicExperimentalFanRange(model)
                    ? "SynapseSourceBackedPresetFixedAndAcCurvesWithFreshDynamicFanRanges" : "NotEstablishedDynamicFanRangesRequireReview", sources),
                .. DynamicFanEvidenceFor(model, sources),
                new("ManualBaselinePerformanceChanges", ExperimentalBladePort.UsesDynamicExperimentalFanRange(model)
                    ? "PresetChangesRequireFreshDestinationRangeContinuity" : "BlockedUntilFanTargetPreservationIsAdmitted", sources),
                new("ChargeLimits50To80Step5AndOff", model.SupportsBatteryProtection
                    ? "SynapseSourceBackedWithoutPhysicalValidation" : "NotEstablished", sources),
                new("MaximumFans", thermal.SupportsMaximumFans ? "SynapseSourceBackedWithoutPhysicalValidation" : "NotEstablished", sources),
                new("AdapterPowerClassification", "SynapseSourceBacked", sources),
                .. OrdinaryThermalEvidenceFor(model),
                .. FanStopEvidenceFor(model),
                new(ExperimentalBladePort.SupportsReviewedMcuLighting(model)
                    ? "CustomFramesAdditionalEffectsAndHyperBoost"
                    : ExperimentalBladePort.SupportsIndependentLighting(model)
                    ? "KeyboardEffectsFramesAndHyperBoost" : "LightingAndHyperBoost", "NotEstablished", sources),
            ];
        }
        IReadOnlyList<ExperimentalFeatureEvidence> features = model.ProductId switch
        {
            0x029F => [
                new("HyperBoost", "SynapseSourceBacked", [
                    "https://apps.razer.com/synapse/products/671/mw/main.2ceb8f5607c0e7a5901a.js",
                    "https://apps.razer.com/synapse/products/671/mw/6737.e123a2b3f0516116025b.js",
                    "https://apps.razer.com/synapse/products/671/mw/rzDevice25.5e330aa9fdf6f29eec62.js"]),
                new("PadlessHyperBoost", "SourceBackedExtrapolation", [
                    "https://apps.razer.com/synapse/products/671/mw/main.2ceb8f5607c0e7a5901a.js",
                    "https://apps.razer.com/synapse/products/671/mw/6737.e123a2b3f0516116025b.js",
                    "https://apps.razer.com/synapse/products/671/mw/rzDevice25.5e330aa9fdf6f29eec62.js"]),
                new("PerformanceAndCustom", "UpstreamOwnerTested", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("FixedFansAndMaximumFans", "UpstreamOwnerTested", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("KeyboardAndLogoLighting", "UpstreamOwnerTested", [MaintainedRazerCtlSource]),
                new("CustomFrames", "UpstreamOwnerTestedWithoutRgbReadback", [MaintainedRazerCtlSource, OpenRazerSource]),
                new("UniformAmbientAndAudio", "SourceBackedExtrapolationWithoutRgbReadback", [MaintainedRazerCtlSource, OpenRazerSource]),
                new("ChargeLimits50To100", "UpstreamOwnerTested", [MaintainedRazerCtlSource])],
            0x029D => [
                new("HyperBoost", "SynapseSourceBackedNotPubliclyListed", [
                    "https://apps.razer.com/synapse/products/669/mw/main.8e09720cb94da405eead.js",
                    "https://apps.razer.com/synapse/products/669/mw/6737.439ff1a12463933a6346.js",
                    "https://apps.razer.com/synapse/products/669/mw/rzDevice25.1094c437ecd0242743f8.js"]),
                new("PadlessHyperBoost", "SourceBackedExtrapolationNotPubliclyListed", [
                    "https://apps.razer.com/synapse/products/669/mw/main.8e09720cb94da405eead.js",
                    "https://apps.razer.com/synapse/products/669/mw/6737.439ff1a12463933a6346.js",
                    "https://apps.razer.com/synapse/products/669/mw/rzDevice25.1094c437ecd0242743f8.js"]),
                new("CustomAndFixedFans", "SourceBackedExtrapolation", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("KeyboardLightingAndLogoOnOff", "UpstreamOwnerReported", [OpenRazerSource, "https://github.com/openrazer/openrazer/pull/2237"]),
                new("LogoStaticAndBreathing", "SourceBackedExtrapolation", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("ChargeLimits50To100", "SourceBackedExtrapolation", [RazerCtlSource, MaintainedRazerCtlSource])],
            0x02C5 => [
                new("BatteryPerformanceModes", "UpstreamOwnerReported", [ExpandedBladeControlSource, "https://github.com/jdsgnrinfo/blade-control/issues/2"]),
                new("CustomPerformance", "SourceBackedExtrapolation", [ExpandedBladeControlSource]),
                new("KeyboardLightingAndLogoOnOff", "UpstreamOwnerReported", [OpenRazerSource, "https://github.com/openrazer/openrazer/pull/2539"]),
                new("LogoStaticAndBreathing", "SourceBackedExtrapolation", [ExpandedBladeControlSource, MaintainedRazerCtlSource]),
                new("ChargeLimits50To100", "SourceBackedExtrapolation", [ExpandedBladeControlSource, MaintainedRazerCtlSource])],
            0x0255 => [
                new("GamingAndCustom", "UpstreamOwnerReported", [ExpandedBladeControlSource]),
                new("SingleZoneRgbLighting", "UpstreamOwnerTested", [ExpandedBladeControlSource])],
            0x02A0 => [
                new("CustomAllLevelCombinations", "SynapseSourceIndependentClusterLevelsExtrapolation",
                    [Blade18SynapseSource,
                     "https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js"]),
                new("UniformAmbientAndAudio", "SharedSoftwareOnObservedFrameTransportWithoutRgbReadback",
                    [Blade18SynapseSource, Blade18ObserverControlsSource]),
                new("RegionalKeyboardLayout", "SynapseSourceBackedEditionGetterWithoutPhysicalValidation",
                    [Blade18SynapseSource,
                     "https://apps.razer.com/synapse/products/672/mw/rzDevice25LedMatrix.21f461fce761cff01be3.js;SHA256=8F8C464818EF7E7AD75F9B18594E74A47D911F5F869C91DF3923AC2880F7F7F6",
                     "https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js;SHA256=DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24"]),
                new("SpatialLighting", "SynapseSourceBackedKeyMapAndEffectAlgorithmsWithoutPhysicalValidation",
                    [Blade18SynapseSource,
                     "https://apps.razer.com/synapse/products/672/ui/static/js/main.0d842862.js",
                     "https://apps.razer.com/synapse/lighting-engine/static/js/main.a383ea9e.js",
                     "Blade18KeyboardLightingLayout.json SHA256 B5C03C722EBA0F57C09D211E4C162F35B7E37A68F0B089E43625C526AD43A1D8",
                     "RzLightingEngineApi_v4.0.55.0.dll SHA256 CD9FC2A61FF920B9B73E1F5E27632020E2F8586F443E426A875AD3B042B714FA"]),
                new("Starlight", "SharedSoftwarePatternOnObservedFrameTransportWithoutRgbReadback",
                    [Blade18SynapseSource, Blade18ObserverControlsSource]),
                new("CustomCpuBoostGpuHigh", "SynapseSourceAndObservedRequestsWithoutIndependentReadback",
                    [Blade18SynapseSource, Blade18ObserverSource,
                     "https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js",
                     "https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js"]),
                new("CustomCpuBoostGpuLow", "SynapseSourceAndObservedRequestsWithoutIndependentReadback",
                    [Blade18SynapseSource, Blade18ObserverFollowupSource,
                     "https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js",
                     "https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js"]),
                new("ChargeLimits50And80", "SynapseObservedRequestsWithoutIndependentReadback", [Blade18ObserverFollowupSource]),
                new("KeyboardBrightness25And100", "SynapseObservedRequestsWithoutIndependentReadback", [Blade18ObserverFollowupSource]),
                new("AdditionalLightingEffects", "SynapseSourceObservedOnly", [Blade18SynapseSource]),
                new("FireAndWheel", "ObservedFrameTransportWithExperimentalPlaybackWithoutRgbReadback",
                    [Blade18SynapseSource, Blade18ObserverControlsSource,
                     "OpenBlade.Protocol.Fixtures.Blade18FireWheelFrames.json;SHA256=4AB4D50E9B7868D8F1E611000ABEF286532924A88B7BCDDCA69EB991582090C4"]),
                new("FunctionLayerAndFnP", "SynapseSourceBackedDefaultShortcutsWithoutPhysicalInputValidation",
                    [Blade18SynapseSource, Blade18MappingSdkSource, Blade18MappingEngineSource,
                     Blade18SynapseHardwareEventParserSource, Blade18ObserverControlsSource,
                     "SysUtilsNative.dll;SHA256=01223BFABF0355F42705836974E343A1F16F837D692999020D218B2615B8E318",
                     "https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/touchpad-enable-or-disable-toggle-button"]),
                new("FixedFanTargets", "SynapseSourceAndObservedRequestsWithSourceBackedTargetReadback",
                    [Blade18SynapseSource, Blade18ObserverFollowupSource, Blade18ObserverControlsSource,
                     "https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js",
                     "https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js"]),
                new("LogoStaticAndOff", "SynapseSourceAndObservedRequestsWithoutIndependentReadback",
                    [Blade18SynapseSource, Blade18ObserverControlsSource]),
                new("MaximumFans", "UpstreamOwnerReported", [Blade18Source, "https://github.com/sqmagellan/razer-ctl/issues/1"]),
                new("KeyboardAndLogoLighting", "UpstreamOwnerReported", [Blade18Source, "https://github.com/sqmagellan/razer-ctl/issues/1"]),
                new("UniformStaticRgb", "SourceBackedExtrapolationWithoutRgbReadback", [OpenRazerSource, MaintainedRazerCtlSource]),
                new("CustomAndChargeLimits50To100", "SourceBackedExtrapolation", [Blade18Source])],
            _ => [],
        };
        if (model.ProductId is 0x029F or 0x029D or 0x02C5)
        {
            features = [.. features, new("HostGridLighting",
                "SoftwarePatternsOnExactModelUpstreamFramesWithoutRgbReadbackOrPhysicalValidation",
                [OpenRazerSource, "https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/driver/razerchromacommon.c#L450"])];
            if (model.ProductId is 0x029D or 0x02C5)
            {
                features = [.. features, new("UniformStaticAmbientAndAudio",
                    "ExactModelUpstreamFramesWithoutRgbReadbackOrPhysicalValidation",
                    [OpenRazerSource, "https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/driver/razerchromacommon.c#L450"])];
            }
        }
        if (model.ProductId == 0x02C5)
        {
            features = [.. features, new("MappedReactiveRippleAndSequence",
                "KeyboardMcuCommonRegionalBindingsWithoutRgbReadbackOrPhysicalValidation",
                ["https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282025%29%20%7C%20RZ09-0530/PI4110-MCUv01.04.00.exe;SHA256=AED7039BE5CC4EC3F166A2728B0C1B90839BAE72C8A04150056654F64F20581F",
                 "Blade14KeyboardLightingLayout.json;SHA256=9356BC177DFBCB458E4C9E465D93AD7E32AF94A61ADBDF7C7E2A7319A7D3E623"])];
        }
        if (ExperimentalBladePort.SupportsStaticSourceHostCurves(model))
        {
            IReadOnlyList<string> curveSources = model.ProductId == 0x029F
                ? [MaintainedRazerCtlSource,
                   "https://apps.razer.com/synapse/products/671/mw/6737.e123a2b3f0516116025b.js;SHA256=54D3A9BBEC83568B02342AA54FF47A94EE55A48ECA85B5D9F28D60D01A0B1A9A",
                   "https://apps.razer.com/synapse/products/671/mw/rzDevice25.5e330aa9fdf6f29eec62.js;SHA256=41E7758114AD3605B5B3EE0E65E79AF3055124F775904902BA3285F710AF55C9"]
                : [MaintainedRazerCtlSource,
                   "https://apps.razer.com/synapse/products/669/mw/6737.439ff1a12463933a6346.js;SHA256=D1AC2212AED5491D3EAB69ABAFA270B963A2336E859DF6BFBF9ED9A455E3791B"];
            features = [.. features, new("StaticSourceHostFanCurves", model.ProductId == 0x029F
                ? "UpstreamMeasuredFloorDeclaredFixedRangeAndSoftwareHostCurveWithoutPhysicalCurveValidation"
                : "SourceExtrapolatedFixedRangeAndSoftwareHostCurveWithoutPhysicalCurveValidation", curveSources)];
        }
        if (model.ProductId is 0x0255 or 0x02C5)
        {
            IReadOnlyList<string> exactSources = model.ProductId == 0x0255
                ? ["https://apps.razer.com/synapse/products/597/mw/main.a1e24f33c1759c4315e3.js;SHA256=A864DC2BE2D35BC3703B81476C1659CAAA4D2C144C89616D9D5D0EE398F030FB",
                   "https://apps.razer.com/synapse/products/597/mw/rzDevice25.ae57bbfa911460c03510.js;SHA256=2F41299C36E60E65D0BFCCE1E666FF67F0FC07F21F4523BED0A4E8CA5AC6043D",
                   "https://apps.razer.com/synapse/products/597/mw/6737.6da70458ec9e64b00f37.js;SHA256=C6E6E76DFB61F960913E22B864A31273280547E21E1462DE3138F096D2DB56EA",
                   "https://apps.razer.com/synapse/products/597/ui/static/js/main.8d820ba2.js;SHA256=ECE69EE8F422A9D919D280C9B4793B2823D5FC7D821AC9DD82519128BA48EAC1"]
                : ["https://apps.razer.com/synapse/products/709/mw/main.bb0dd1873a58a987c63a.js;SHA256=CC8E0EFD2B37F1ED5AF6C8CC8C3AB1327B9502927DC05F20EE7465486AB006F1",
                   "https://apps.razer.com/synapse/products/709/mw/rzDevice25.d532642d8de7a9cfcf5d.js;SHA256=9109826D790FEB8597BBFC115231066CA66E2C480ED9769BB85F3F91188AAD0A",
                   "https://apps.razer.com/synapse/products/709/mw/6737.d34a3f9a638030f35c6a.js;SHA256=4758FB7E2D8296564FB9CE971F6DCE3D580ED5407EEF07A69E1C362A04FF28C0",
                   "https://apps.razer.com/synapse/products/709/ui/static/js/main.fd91712e.js;SHA256=338D8F51C9A1179D3B2436F77FD44B3D393C3CC987D0EC73928E1F636BF3A9B7",
                   "https://apps.razer.com/synapse/products/709/ui/static/js/2915.4e513855.chunk.js;SHA256=6D6106090CDC8D26F527F0265C07DBB4035EC57FBD0BDC41F0EDB1A205ADCEB2"];
            features = [.. features, .. DynamicFanEvidenceFor(model, exactSources),
                new("FixedAndCurveFanWrites", "SynapseSourceBackedPresetFixedAndAcCurvesWithFreshDynamicFanRanges", exactSources),
                new(model.ProductId == 0x0255 ? "CreatorPerformanceRaw2" : "MaximumPowerPerformanceRaw2",
                    "SynapseSourceBackedWithoutPhysicalValidation", exactSources)];
        }
        features = [.. features, .. FanStopEvidenceFor(model), .. OrdinaryThermalEvidenceFor(model)];
        return model.AdapterPowerEvidenceSource is { } source
            ? [.. features, new("AdapterPowerClassification", "SynapseSourceBacked", [source])]
            : features;
    }

    private static IEnumerable<ExperimentalFeatureEvidence> OrdinaryThermalEvidenceFor(ExperimentalBladeModel model) =>
        OrdinaryThermalPowerAdmission.SupportsProduct(model.ProductId)
            ? [new(OrdinaryThermalPowerAdmission.FeatureName,
                OrdinaryThermalPowerAdmission.EvidenceBasis, [OrdinaryThermalPowerAdmission.EvidenceSource])]
            : [];

    private static IEnumerable<ExperimentalFeatureEvidence> DynamicFanEvidenceFor(
        ExperimentalBladeModel model, IReadOnlyList<string> sources) =>
        !ExperimentalBladePort.UsesDynamicExperimentalFanRange(model) ? []
            : ExperimentalBladePort.GetExperimentalFanRangesByPerformancePreset(model).Select(pair =>
                new ExperimentalFeatureEvidence(
                    $"ManualFanRange{pair.Key}{pair.Value.MinimumRpm}To{pair.Value.MaximumRpm}Step{pair.Value.StepRpm}",
                    pair.Key == PerformancePreset.BatteryBalanced
                        ? "SynapseSourceBackedFixedOnlyWithFreshDynamicRangeAndBatteryAgreement"
                        : "SynapseSourceBackedFixedAndHostCurveWithFreshDynamicRangeWithoutPhysicalValidation", sources));

    private static IReadOnlyList<ExperimentalFeatureEvidence> FanStopEvidenceFor(ExperimentalBladeModel model)
    {
        IReadOnlyList<FanStopModelContract> contracts = FanStopModelContract.ForProfile(
            model.ModelNumber, model.ProductId, model.ProductName);
        return contracts.Count == 0 ? [] :
            [new("FanStopAndGuardianAuto", "FirmwareSourceBackedWithoutPhysicalValidation",
                ["https://github.com/OSSBlade/openblade-core/blob/main/docs/fan-stop-model-coverage.md",
                    .. contracts.Select(contract => $"{contract.FirmwareIdentity};SHA256={contract.FirmwareSha256}")])];
    }

    public static IReadOnlyList<ExperimentalBladeModel> All { get; } = Array.AsReadOnly<ExperimentalBladeModel>(
    [
        new("RZ09-0483", "Blade 16 - RZ09-0483", 0x029F, true, RazerCtlSource,
            "https://apps.razer.com/synapse/products/671/mw/main.2ceb8f5607c0e7a5901a.js"),
        // Published DMI report: https://img.unikoshardware.com/wp-content/uploads/2023/06/RAZER_BLADE_14Report.htm
        new("RZ09-0482", "Blade 14 - RZ09-0482", 0x029D, true, RazerCtlSource,
            "https://apps.razer.com/synapse/products/669/mw/main.8e09720cb94da405eead.js"),
        // Published system model: https://browser.geekbench.com/v6/cpu/compare/14894027?baseline=2340230
        new("RZ09-0530", "Blade 14 - RZ09-0530", 0x02C5, true, BladeControlSource,
            "https://apps.razer.com/synapse/products/709/mw/main.bb0dd1873a58a987c63a.js"),
        // Windows System Model reported by an owner:
        // https://learn.microsoft.com/en-us/answers/questions/3810665/intel-wi-fi-6-ax201-is-not-working
        new("RZ09-0328", "Blade 15 Base Model (Early 2020) - RZ09-0328", 0x0255, false, BladeControlSource,
            "https://apps.razer.com/synapse/products/597/mw/main.a1e24f33c1759c4315e3.js"),
        // Exact-model owner report: https://github.com/sqmagellan/razer-ctl/issues/1
        // Windows product: https://browser.geekbench.com/v6/cpu/6428586
        new("RZ09-0484", "Blade 18 - RZ09-0484", 0x02A0, true, Blade18Source,
            "https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js"),
        CreateSourceReviewed("RZ09-0508", "Blade 14 - RZ09-0508", 0x02B6,
            "main.1b1b68e07824e5af9833.js", "rzDevice25.06135650b7453937e0e8.js",
            "0E5BD5C21DD610E5D889F608B2EBB1D80BFC7CA90237FBBC3A292ADBAF06EF90", true, cpuBoost: true),
        CreateSourceReviewed("RZ09-0510", "Blade 16 - RZ09-0510", 0x02B7,
            "main.27df2e7ddab0b6bc34cc.js", "rzDevice25.60bfcef58690c8bb715d.js",
            "2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491", true, cpuBoost: true),
        CreateSourceReviewed("RZ09-0509", "Blade 18 - RZ09-0509", 0x02B8,
            "main.ded18de8aee09a8f8238.js", "rzDevice25.6339454cb5b2fe587300.js",
            "8780AA380D11665A2D551174D162C7278404465772219D0530A29B09E297BC81", true),
        CreateSourceReviewed("RZ09-0529", "Blade 18 - RZ09-0529", 0x02C7,
            "main.1f8e0c604f19af3ea24c.js", "rzDevice25.c8da1ead63e6cf002d32.js",
            "02C55B78B3EABDCC104D725C64C94CADE500FA78D29713ACE5FEF520D243EBB9", false,
            batteryPresets: true, maximumPower: true),
        CreateSourceReviewed("RZ09-0370", "Blade 14 - RZ09-0370", 0x0270,
            "main.c661207b6abe5eb319cb.js", "rzDevice25.1672779ed3e58b9415ca.js",
            "F696504C78559A2E04E503F8CF7B069D87F1298BFB767677912CD649EEB49456", false, cpuBoost: true, turbo: false,
            batteryProtection: false, silent: false, balancedDc: false),
        CreateSourceReviewed("RZ09-0427", "Blade 14 (2022) - RZ09-0427", 0x028C,
            "main.825dec01b9aefa0b84ac.js", "rzDevice25.ceef38d32121535173d9.js",
            "D94B40BB4B1AEDD17C0028A625C28D00D8BC14B26361CBF25133A3CE4E5B0C67", false, cpuBoost: true, turbo: false),
        CreateSourceReviewed("RZ09-0421", "Blade 15 (2022) - RZ09-0421", 0x028A,
            "main.3650e97f35689328d442.js", "rzDevice25.33bbbe9e1d2d72bdf1a1.js",
            "CEF5D482E96F2833C84998E2FE998D0A41AA7CA4483E4E69156424F76475D513", false, cpuBoost: true, turbo: false),
        CreateSourceReviewed("RZ09-0423", "Blade 17 (2022) - RZ09-0423", 0x028B,
            "main.8378c0c79039a3f2ba23.js", "rzDevice25.7ffcfe7d1ddb6103ac44.js",
            "A9442C86AB7D8F0549E18016324462A52A040E5578410F296BDF72884A383962", false, cpuBoost: true, turbo: false),
        // Exact 2019 Pro 17 DMI and product 564 guide establish this profile.
        // Only reviewed thermal HID controls are admitted; native GPU clocks remain outside scope.
        CreateSourceReviewed("RZ09-0287", "Blade Pro 17 (2019)", 0x0234,
            "main.cd82ad6254565b1185d9.js", "rzDevice25.c85377977ca9cfdbb0b0.js",
            "65C38CDAAFC263682BADF4695C9239DF96FA1F26091274FB4AE0E81C5ACD8C17", false,
            turbo: false, silent: false, balancedDc: false, batteryProtection: false),
        // The same owner report pairs this exact DMI product with 1532:024C.
        // https://forum.manjaro.org/t/login-screen-not-showing-up-50-of-time-nvidia/83863
        CreateSourceReviewed("RZ09-0314", "Blade Pro 17 (Fall 2019)", 0x024C,
            "main.aef91a9c8a23c54b3711.js", "rzDevice25.c9cde47d1f5b9618158b.js",
            "B299E84011BB04545123498F4B17DC0F774CB2560BF21C2F2BE7B9393B416AE5", false,
            turbo: false, silent: false, balancedDc: false, batteryProtection: false),
        // Exact Mid 2021 model source exposes CPU Boost 3 and GPU levels 0-2 on AC.
        CreateSourceReviewed("RZ09-0409", "Blade 15 Advanced Model (Mid 2021) - RZ09-0409", 0x0276,
            "main.6098ef0f83a9cd1c24a9.js", "rzDevice25.7fc62fb8a269279f7b9e.js",
            "940EE59B6B034BEE82B77779323617F817D8D400F7BF526058BFD10CDC1568BA", false,
            cpuBoost: true, turbo: false, silent: false, balancedDc: false, batteryProtection: false),
        // CPU Boost requires an EC update; no readable exact-model EC admission is established.
        CreateSourceReviewed("RZ09-0330", "Blade 15 Advanced Model (Early 2020) - RZ09-033", 0x0253,
            "main.11dfb9173b8a1c795ffd.js", "rzDevice25.1dbf16d259718708f12a.js",
            "ED1BEF8C07D7E5BBFEEFE235179B351B85BFAE6F1BCC3A0A131F7A99BB157E1C", false, turbo: false, silent: false, balancedDc: false,
            batteryProtection: false),
        // Only typed HID thermal presets; vendor native GPU clock changes are not admitted.
        CreateSourceReviewed("RZ09-0351", "Blade 15 Base Model (Late 2020)", 0x0268,
            "main.f9ef043eaf3feceba719.js", "rzDevice25.9c9299b0f9660c882e16.js",
            "330835150F88BC505B181EAC4828506750CD7365F9F4CB242437CF3CFC133A24", false, turbo: false, silent: false, balancedDc: false,
            batteryProtection: false, custom: false, gaming: true, creator: true),
        // RZ09-0301 is Mid 2019 Advanced (Razer answer 1580). Product 581
        // explicitly admits ordinary and Mercury editions; the original Mid 2019
        // Advanced owner report corroborates 0245 (OpenRGB issue 199).
        // Its GPU "wattage" selector independently uses the same bounded 0D/07
        // bytes as advanced levels. Vendor DLL GPU clocks remain unadmitted.
        CreateSourceReviewed("RZ09-0301", "Blade", 0x0245,
            "main.450b813b36b314f7abf0.js", "rzDevice25.f668146da6ee1d0b3b6c.js",
            "8459C76B984DDF5894CC7C7E00A40B76EEDE5E32D65DF2F7D6D75A541C36DAC7", false,
            turbo: false, silent: false, balancedDc: false, batteryProtection: false),
        // The exact DMI name and product 582 guide jointly identify RZ09-0300.
        CreateSourceReviewed("RZ09-0300", "Blade 15 Mid 2019-Base", 0x0246,
            "main.c9ac81170313d8740ddc.js", "rzDevice25.56d11fe9830f93920735.js",
            "A5F75408C2D859ACF0F3418B950989271E98DFEE5056DD17DCA6BA5557FDD4C6", false,
            turbo: false, silent: false, balancedDc: false, batteryProtection: false,
            custom: false, gaming: true, creator: true),
    ]);

    private static ExperimentalBladeModel CreateSourceReviewed(string number, string name, ushort product,
        string mainFile, string commandFile, string commandHash, bool maximumFans,
        bool batteryPresets = false, bool maximumPower = false, bool cpuBoost = false,
        bool turbo = true, bool silent = true, bool balancedDc = true, bool batteryProtection = true,
        bool custom = true, bool gaming = false, bool creator = false)
    {
        string root = $"https://apps.razer.com/synapse/products/{product}/mw/";
        return new(number, name, product, batteryProtection, root + mainFile, root + mainFile,
            new(root + commandFile + ";SHA256=" + commandHash, maximumFans, cpuBoost, batteryPresets, maximumPower,
                SourceReviewedEvidence(product), turbo, silent, balancedDc, custom, gaming, creator));
    }

    // Exact acquisition pins are source provenance, not installed firmware admission.
    private static string ReviewedMcuLightingSource(ExperimentalBladeModel model) => model.ProductId switch
    {
        0x0245 => "https://rzr.to/KkxlH;SHA256=58F880CC46A64379131E7EDC3D1A8C63418DC2989179D3A514D6D79EA38E49E5",
        0x028A => "https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20%282022%29%20%7C%20RZ09-0421x/RazerUpdater_v1.1.1.3_CH580_EC_0103_MCU_010300_v2.exe;SHA256=6FCFD96615C514C6CD25944405980B6B39D8A2B0458F8CABCE299DAC37728118",
        0x028B => "https://dl.razerzone.com/drivers/Razer%20Blade%2017%22%20%282022%29%20%7C%20RZ09-0423x/RazerUpdater_v1.1.1.3_DI780-ECv0105_MCUv010200_VBIOS_v3.exe;SHA256=3F096851BED1679A83630FA588DAF0D977F0944634075E75493658CFAD03C554",
        0x028C => "https://dl.razerzone.com/drivers/Razer%20Blade%2014%20%282022%29%20%E2%80%93%20RZ09-0427/RazerUpdater_v1.1.1.3_PI480-MCUv010100-PDv11-BIOSv3.06v1-ECv0106.exe;SHA256=6DC6495A1BDEBF7AEA343F2A5F1E82E5565AECE7D1FBF50CBD2E450CC18B04EF",
        0x02B7 => "https://dl.razerzone.com/drivers/Razer%20Blade%2016%22%20%282024%29%20%7C%20RZ09-0510/RazerUpdater_v1.1.1.3_SO6100_EC_0102_MCU_010300_VBIOS.exe;SHA256=2912408C872D57176E42D2DA09B68566CECF9B723F51CCE4AE4E717A9E98E295",
        0x02B8 => "https://dl.razerzone.com/drivers/RazerBlade18RZ09-0509/RazerUpdater_v1.1.1.3_KI8100_EC_1.02_MCU_010100v2.exe;SHA256=6E06D131E1A77A733FBF5C37E5BA95CCDA2E496257A7BB4747B1E274DDA74C38",
        0x02C7 => "https://dl.razerzone.com/drivers/RazerBlade18RZ09-0529/KI8110-MCUv01.07.00.exe;SHA256=30C06BA3D2D057593BBF3C812249D4EDD020D17FC157A8CC7C29264A9E72F9DC",
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    private static System.Collections.Immutable.ImmutableArray<string> SourceReviewedEvidence(ushort product) =>
        product switch
        {
            564 => [
                "https://apps.razer.com/synapse/products/564/mw/main.cd82ad6254565b1185d9.js;SHA256=E8E61CE165B2E724DE80C9FB2F8AA2D6F3D3CF8B89AF0D1F1866C5624A3FD48C",
                "https://apps.razer.com/synapse/products/564/mw/6737.09466ede91c383e63db5.js;SHA256=578C055A626C97F320A8642DDCF6D727EDEC267C02BF4BDF68D9921A7DF92A8F",
                "https://apps.razer.com/synapse/products/564/mw/rzDevice25.c85377977ca9cfdbb0b0.js;SHA256=65C38CDAAFC263682BADF4695C9239DF96FA1F26091274FB4AE0E81C5ACD8C17",
                "https://apps.razer.com/synapse/products/564/mw/rzDevice25LedMatrixBlade.cf43cdc9a0705593c034.js;SHA256=EEF7D6D017DA455DCDB3A283F157256B7BE3FE033BCC589647E24C67BB58217A",
                "https://apps.razer.com/synapse/products/564/ui/static/js/main.c6dfa6fe.js;SHA256=1E832E271629A0B9924BD0E47D9C9B6C71ACA2AEEA49301B8E804AB31A1CD3C3",
                "https://apps.razer.com/synapse/products/564/mw/rzDevice25LedMatrix.07dcd79a57a435233647.js;SHA256=A2B50251DBB0960C41954A3676D7801F800B8370457AA903039E9D64BC039929"],
            588 => [
                "https://apps.razer.com/synapse/products/588/mw/main.aef91a9c8a23c54b3711.js;SHA256=58C8699616E125BE2F8F4FFFD490FFCD632F30EC171898229BA76275701558ED",
                "https://apps.razer.com/synapse/products/588/mw/6737.486962f684c3054bcd0c.js;SHA256=62AC7270B6927323605CDF45D699EA07E48C1063FC6F5642F2FC4ABC6CDD12FA",
                "https://apps.razer.com/synapse/products/588/mw/rzDevice25.c9cde47d1f5b9618158b.js;SHA256=B299E84011BB04545123498F4B17DC0F774CB2560BF21C2F2BE7B9393B416AE5",
                "https://apps.razer.com/synapse/products/588/mw/rzDevice25LedMatrixBlade.a3ca67aee2e5d66c4d8a.js;SHA256=009DA3FDDF6D567F09F5DFFF330FF88F9BC56F11844D72557C3D6DD89C0F5DC2",
                "https://apps.razer.com/synapse/products/588/mw/rzDevice25LedMatrixBladeWattage.6400257deb0580f370f5.js;SHA256=A0F2E3A06ABE8BFBABE561F5808BDA5B175857E8C38D8E0460B74D149BF0D330",
                "https://apps.razer.com/synapse/products/588/ui/static/js/main.5ff20197.js;SHA256=2FA82D9AC334FF3453E73676CEDBA0532093C90BDE0B0F9030EE57A47CC707A0",
                "https://apps.razer.com/synapse/products/588/mw/rzDevice25LedMatrix.16e169229a175ac38a8c.js;SHA256=F22899E74898C2007C53BA61A64F474297527123CA600680E902D37DDEF0DA9E"],
            630 => [
                "https://apps.razer.com/synapse/products/630/mw/main.6098ef0f83a9cd1c24a9.js;SHA256=57D83A4D3928BE973205AF873268D65662A1833452165EA8199F816D27BAB51B",
                "https://apps.razer.com/synapse/products/630/mw/6737.71d0d0b36aad77ffb446.js;SHA256=80A872631CBD5E16F713467DA13B9B7D76889487E22A6CE3DB854A15E1631558",
                "https://apps.razer.com/synapse/products/630/mw/rzDevice25.7fc62fb8a269279f7b9e.js;SHA256=940EE59B6B034BEE82B77779323617F817D8D400F7BF526058BFD10CDC1568BA",
                "https://apps.razer.com/synapse/products/630/mw/rzDevice25LedMatrixBlade.5258f7f0a1f14ad07643.js;SHA256=494AD54804E7DBFB10CA6D405EB4A307B6DDF614BC5013C01280135E4CAA972F",
                "https://apps.razer.com/synapse/products/630/mw/rzDevice25LedMatrixBladeWattage.cb42c79fefbfdc6d1216.js;SHA256=E75F29F664214B9F08295F22CBDB2B75614E0D0240B0D0044E18DE2ED1621325",
                "https://apps.razer.com/synapse/products/630/ui/static/js/main.64a01e0e.js;SHA256=1C5A6D450F5E544FE8855FFF92D95BC3ABCE2697574CC58D37AA76817D9F95E5",
                "https://apps.razer.com/synapse/products/630/mw/rzDevice25LedMatrix.0bdbcce58345eb489009.js;SHA256=CAFDC975CC5A5E1B69EE0145B84B91B52EF6AFDABE19BF2B481067A58A3B8279"],
            581 => [
                "https://apps.razer.com/synapse/products/581/mw/main.450b813b36b314f7abf0.js;SHA256=F7A863CF5E95D94539ABD3F4C29076E1710901B5D02624EFDC0275265C651D7A",
                "https://apps.razer.com/synapse/products/581/mw/6737.f49b9965ecd5cf011e43.js;SHA256=5B11C5CD3E26BBD74DAC7A98413AB31315DAFEFAD5B783DBF0CF014E6A33D017",
                "https://apps.razer.com/synapse/products/581/mw/rzDevice25.f668146da6ee1d0b3b6c.js;SHA256=8459C76B984DDF5894CC7C7E00A40B76EEDE5E32D65DF2F7D6D75A541C36DAC7",
                "https://apps.razer.com/synapse/products/581/mw/rzDevice25LedMatrixBladeWattage.09358e38f5994093ae20.js;SHA256=EB94694DBB49AE38AD1837AC926D3D78515F9F501B249B68FE48C200250036F5",
                "https://apps.razer.com/synapse/products/581/ui/static/js/main.98cf2efe.js;SHA256=C29CE6F4E66E5658B9B7FC4443AE27057EFA4C06DF3A96F6C0E70B79E712DB85",
                "https://apps.razer.com/synapse/products/581/mw/rzDevice25LedMatrix.dadaa67383371e256a18.js;SHA256=BEB240485210034445236483B1226DBCCEC67A5C8AB216C9CF976B57F604A1E6",
                "https://apps.razer.com/synapse/products/581/mw/rzDevice25LedMatrixBlade.211294bf38840fcb516b.js;SHA256=7F25B39B209807B8582E30717B90129AAB0B3B20B01DE5B0465C2819D4FD99DF",
                "https://apps.razer.com/synapse/products/581/mw/3159.f364bf525336e07016a6.js;SHA256=9099AB575124FDDCE557262C2D6DD4A67F872F678127D89AFBE8DF8781A4EDDF",
                "https://apps.razer.com/synapse/products/581/mw/3689.30a0792a28654ef8ef1c.js;SHA256=E9CFC508E4D6C81671C7A57081DA3DD640398B191AF87B761A17CCDF04B0440C"],
            582 => [
                "https://apps.razer.com/synapse/products/582/mw/6737.3d32e8efb681572a4d05.js;SHA256=EFFE1404E33611F8B26AA248B64A7B25A1F9283217BE1CC5C85DEEC98AB10B6E",
                "https://apps.razer.com/synapse/products/582/mw/main.c9ac81170313d8740ddc.js;SHA256=EAEE32B144B086165E43FC97446378D22919FB19360826028A0434B03D7190C4",
                "https://apps.razer.com/synapse/products/582/mw/rzDevice25.56d11fe9830f93920735.js;SHA256=A5F75408C2D859ACF0F3418B950989271E98DFEE5056DD17DCA6BA5557FDD4C6",
                "https://apps.razer.com/synapse/products/582/mw/rzDevice25LedMatrixBladeWattage.45c847b37f79a031cbaf.js;SHA256=71D91DFF87CB617384535C5DEC4643A2CC551C3CDE63EE691A29D7282FF96AB2",
                "https://apps.razer.com/synapse/products/582/ui/static/js/main.6ce6c0b6.js;SHA256=B4BEB1A616F01AE0F9DD29741B6AE6A66ED794CD2E109C92E57D9BBF232FDF1F",
                "https://apps.razer.com/synapse/products/582/mw/rzDevice25LedMatrix.e2382008c1993f0aa740.js;SHA256=A679866CD21F4F47E645C495F5026C3083A2E163E98F64898FC869C2EC995EAF",
                "https://apps.razer.com/synapse/products/582/mw/rzDevice25LedMatrixBlade.a4db46356fdb831037e2.js;SHA256=B43A923BCF590E180CBF7E1F8143CD9F3E4F96FE1045E690E924D3CE7D13EB9C"],
            595 => [
                "https://apps.razer.com/synapse/products/595/mw/6737.6a4f810f43bf4dd7ea5d.js;SHA256=AC6F2091BC3ABE952B1AA7D8F156F2C6859E3195112A06D15AA03BB41FA81F6A",
                "https://apps.razer.com/synapse/products/595/mw/main.11dfb9173b8a1c795ffd.js;SHA256=95A5CB17A346AC87CBF033E734EC18955E8B42BBFD572B666AAED36FBB3342D1",
                "https://apps.razer.com/synapse/products/595/mw/rzDevice25.1dbf16d259718708f12a.js;SHA256=ED1BEF8C07D7E5BBFEEFE235179B351B85BFAE6F1BCC3A0A131F7A99BB157E1C",
                "https://apps.razer.com/synapse/products/595/mw/rzDevice25LedMatrixBladeWattage.372543c0af163eb3248d.js;SHA256=E2212888DAC1E9026061079E38464221F38E72A17B71EE059902C6065851FE3F",
                "https://apps.razer.com/synapse/products/595/mw/rzDevice25LedMatrixBlade.79a5d89deb2d7a89f414.js;SHA256=5DABD32211A6FD805ADF1FD5DA0921CDA6D269A43A6A2B4E1CB4EA803BD92A8E",
                "https://apps.razer.com/synapse/products/595/mw/rzDevice25LedMatrix.c2826f7990981b78f559.js;SHA256=D24550DC01703E0BF6E17BA971DA7EB877A9AFDF45F0E15407E432F68C116CC7",
                "https://apps.razer.com/synapse/products/595/ui/static/js/main.cb0bb66b.js;SHA256=F18BBCF3BB5A24FEC8272213DE8AA93E55FFA8E9952F90F5556C3DE62E79EE08"],
            616 => [
                "https://apps.razer.com/synapse/products/616/mw/2592.2faacaabe55016689a38.js;SHA256=D7C0644CA124900DB2A57812E85CF94C0C75A75ACE11CAB903B11A81438B4B92",
                "https://apps.razer.com/synapse/products/616/mw/main.f9ef043eaf3feceba719.js;SHA256=ABC074EB488E0848E4751D8417D6B3EF3BA02FAC06C9C98ED32B5E1E1350B2C7",
                "https://apps.razer.com/synapse/products/616/mw/rzDevice25.9c9299b0f9660c882e16.js;SHA256=330835150F88BC505B181EAC4828506750CD7365F9F4CB242437CF3CFC133A24",
                "https://apps.razer.com/synapse/products/616/mw/rzDevice25LedMatrixBladeWattage.f57ee707817ebfa7e2d8.js;SHA256=51BC8EF2FEFDA5FE8263E26F175F20B25BF0703CF0EEA8E9DE0D7B8AB1959426",
                "https://apps.razer.com/synapse/products/616/ui/static/js/main.700be592.js;SHA256=A802348F6BA596483A820A90D583C3680903C74AEB1CCB559174CBEB47C0E2F2",
                "https://apps.razer.com/synapse/products/616/mw/rzDevice25LedMatrix.4c26238bf2faca60f170.js;SHA256=90578F207DC08DFD31291281D4381E442426757900A061FAAA97D9ABAFD99D8B",
                "https://apps.razer.com/synapse/products/616/mw/rzDevice25LedMatrixBlade.4495de9ea9ea03b9e8de.js;SHA256=3B48422C14BDC098904459710AF66336429EC4350623C197A6994CCC4433D4C9"],
            624 => [
                "https://apps.razer.com/synapse/products/624/mw/6737.6b47e5cb8059a1bdacfb.js;SHA256=C36EFB8BD1F15DC65F8C235895EFA5F20A59D71D45BC6D22BF92D88C9C8D79F7",
                "https://apps.razer.com/synapse/products/624/mw/main.c661207b6abe5eb319cb.js;SHA256=50DFF3488639F22BD3890596A047378D14CB52334A49EDDCEDDAFC7EAF0669C2",
                "https://apps.razer.com/synapse/products/624/mw/rzDevice25.1672779ed3e58b9415ca.js;SHA256=F696504C78559A2E04E503F8CF7B069D87F1298BFB767677912CD649EEB49456",
                "https://apps.razer.com/synapse/products/624/ui/static/js/main.2709843a.js;SHA256=48359BAFE13C56156017A81A72E82CEDB3F44BAD5A52BB921D4FBE96E1F1A1FE",
                "https://apps.razer.com/synapse/products/624/mw/rzDevice25LedMatrix.41a9a87e480e7e8e752a.js;SHA256=9BBA49FAE78CF93BB397A4EE76BDD147234C83DA5BFE63FF412A25E2143125CE",
                "https://apps.razer.com/synapse/products/624/mw/rzDevice25LedMatrixBlade.c61a8d4fcda5f10441ea.js;SHA256=9293920B3D6B4065F4539972C8A69E87FF3C79354309F6BA214FA4D8BD430CAF",
                "https://apps.razer.com/synapse/products/624/mw/rzDevice25LedMatrixBlade2.58b0677575a642f3900f.js;SHA256=1D0A0B7FDB95DE34A8D14BB9F3E8C20B3D84E9F68A5D8470BEA5FA8621CEE958",
                "https://apps.razer.com/synapse/products/624/mw/rzDevice25LedMatrixBladeWattage.42a7744269064ea241c2.js;SHA256=A83F5FE396DABF9D5FB1AA1DAF77CC65E0927BE2D23EDDD4CC3A19633AC002F5"],
            652 => [
                "https://apps.razer.com/synapse/products/652/mw/6737.4b526d5a88f50676de59.js;SHA256=B1A181C2F4A95C1871A5AEEDC5F820DBC991A61A067CB060BAC9ABEBF04EE488",
                "https://apps.razer.com/synapse/products/652/mw/main.825dec01b9aefa0b84ac.js;SHA256=77AD6A8F51278C2950D07AAB3CA4E98EC67001527067F4118F50AF00954EABA1",
                "https://apps.razer.com/synapse/products/652/mw/rzDevice25.ceef38d32121535173d9.js;SHA256=D94B40BB4B1AEDD17C0028A625C28D00D8BC14B26361CBF25133A3CE4E5B0C67",
                "https://apps.razer.com/synapse/products/652/ui/static/js/main.ca1e43bb.js;SHA256=9C640FF7F3359DFB8F8D7AA005DB01336F5C05A4EFB25C219550BA6D05A6C407",
                "https://apps.razer.com/synapse/products/652/mw/rzDevice25LedMatrix.b6323801c081b7493b2b.js;SHA256=9D59660DEE9014DDFFC0EDEE828B402F906320232C37559DFFEA1B5DB1947519",
                "https://apps.razer.com/synapse/products/652/mw/rzDevice25LedMatrixBlade.af4e2a8f0926c1f15907.js;SHA256=DD844382088CD971CD0D831D06221F9F05230083503271BC52C1C5BD9F5BE626",
                "https://apps.razer.com/synapse/products/652/mw/rzDevice25LedMatrixBlade2.032e7086bcdb3c4d6e9d.js;SHA256=96F6D3F024A5217FDCF7C6604D3EDC324CA1C7D0D0DBCF390851CD3B2AC45CC2",
                "https://apps.razer.com/synapse/products/652/mw/rzDevice25LedMatrixBladeWattage.2c379fa5a66007f7b183.js;SHA256=9B07B08CEFCAE9288DCF37BBD65D28B4DEBFCB92F14FE7BC08172F1BD7634447"],
            650 => [
                "https://apps.razer.com/synapse/products/650/mw/6737.1cc98c1e1650ffce7912.js;SHA256=8B31CFB0033A9C75F5E6E69D914A3A36BAE0A4944886B5CC6D076329434D1DF9",
                "https://apps.razer.com/synapse/products/650/mw/main.3650e97f35689328d442.js;SHA256=270D5D4E4AF9674D2219EFBE609294DC46E1BAF4E08A14AF32306D4495CB5A98",
                "https://apps.razer.com/synapse/products/650/mw/rzDevice25.33bbbe9e1d2d72bdf1a1.js;SHA256=CEF5D482E96F2833C84998E2FE998D0A41AA7CA4483E4E69156424F76475D513",
                "https://apps.razer.com/synapse/products/650/ui/static/js/main.90e358d5.js;SHA256=06C52972C023B65B11DDF1C379671526E96C68B46105FC3EDCC714F0EF4944B0",
                "https://apps.razer.com/synapse/products/650/mw/rzDevice25LedMatrix.e196c7f06b29ad9b9887.js;SHA256=2594338071860022682DA7363407B8CD204AF8C6196FE979D00AE1BE1F9557BD",
                "https://apps.razer.com/synapse/products/650/mw/rzDevice25LedMatrixBlade.a041b7342ce44c78ec3b.js;SHA256=F0E34A36BC2A09A953835DE05F83C545CF18B11F139C86D0A0055942F8FBEA32",
                "https://apps.razer.com/synapse/products/650/mw/rzDevice25LedMatrixBlade2.71032c5b3b3df82a387d.js;SHA256=E19A44993E93CAC2E79FB0FF9242CCC735A8283344CCDFA57E8E42180B29601E",
                "https://apps.razer.com/synapse/products/650/mw/rzDevice25LedMatrixBladeWattage.a715fa3adc2631c080dc.js;SHA256=70687428706956400D3CB5227E35BB03EFFB13D75E70429E06A94A7369202306"],
            651 => [
                "https://apps.razer.com/synapse/products/651/mw/6737.003892ae34f5de0292c0.js;SHA256=E8A2CFF2F19A59B8F425E7EC70BFF5873551479560005435E1B04B34F9CC7454",
                "https://apps.razer.com/synapse/products/651/mw/main.8378c0c79039a3f2ba23.js;SHA256=F227CBA8959DFEE0B898077D4CC77651066B7BFFF8CAA20910E9B3946E026AC4",
                "https://apps.razer.com/synapse/products/651/mw/rzDevice25.7ffcfe7d1ddb6103ac44.js;SHA256=A9442C86AB7D8F0549E18016324462A52A040E5578410F296BDF72884A383962",
                "https://apps.razer.com/synapse/products/651/ui/static/js/main.8fce5f78.js;SHA256=FF4ACE62F536DAD8E14A674177638FAC74ED462C74FECC7A42D66B2F3616C254",
                "https://apps.razer.com/synapse/products/651/mw/rzDevice25LedMatrix.91ecd705f076a69cb74b.js;SHA256=40237B74FDC3A2F05F47C05501117EF1E97131C88055614D9A9ED58DAB96EF31",
                "https://apps.razer.com/synapse/products/651/mw/rzDevice25LedMatrixBlade.3d7a6466eb25ba2e5f2d.js;SHA256=66A45A45F7B471426FE41875D2C84E7F0EFD62CCB1066CF3657949B02A163C27",
                "https://apps.razer.com/synapse/products/651/mw/rzDevice25LedMatrixBlade2.82d68898207246f09200.js;SHA256=C92ED2B6966AEEEE991819C291A5E054F9EC04B72D8202C1435A76DBC8219945",
                "https://apps.razer.com/synapse/products/651/mw/rzDevice25LedMatrixBladeWattage.d708c5b081c8f56a50b6.js;SHA256=204DB11C43D4ECFC33EC476A770AC246FB19085A001140582A08831CBE77689D"],
            694 => [
                "https://apps.razer.com/synapse/products/694/mw/6737.63910b6756fd6d249dc6.js;SHA256=488D72368F0EBFB2A4DD80BE68E8C2BAB8020CBBCC65CA786757FEA79507B997",
                "https://apps.razer.com/synapse/products/694/mw/main.1b1b68e07824e5af9833.js;SHA256=F1AEEAFF4BBDB631B4E66BF8B2B6415FB0EADDF24343A146787B091F89AA7BDC",
                "https://apps.razer.com/synapse/products/694/mw/rzDevice25.06135650b7453937e0e8.js;SHA256=0E5BD5C21DD610E5D889F608B2EBB1D80BFC7CA90237FBBC3A292ADBAF06EF90",
                "https://apps.razer.com/synapse/products/694/ui/static/js/main.415cc878.js;SHA256=C22750AAD6E54EE86E04B3D7F7D7DA520636293C209734358FC26A744D84F6B1",
                "https://apps.razer.com/synapse/products/694/mw/rzDevice25LedMatrix.0f011b08f1cdafea199c.js;SHA256=E60132332EC11942F9FAB607ECC7085220157EE6E701FC03C7FAA4BC40A1B98E",
                "https://apps.razer.com/synapse/products/694/mw/rzDevice25LedMatrixBlade.b65ad2e4cc7347193091.js;SHA256=2D630C25A05AD475E2E382CB653FC281A972DCAD6C2465ECAE387800A4EA0AA2",
                "https://apps.razer.com/synapse/products/694/mw/rzDevice25LedMatrixBlade2.f195d8975ab02884ce66.js;SHA256=2C67D03295574A782387614E2BE285D16976A325A70AF1C4227E054DE83510ED",
                "https://apps.razer.com/synapse/products/694/mw/rzDevice25LedMatrixBladeWattage.a4b526ca0148004c643a.js;SHA256=B808AC85FBFBA3B2BF7C8A0593DD38520DF005D309E66C80A28B7D7122D95EB4"],
            695 => [
                "https://apps.razer.com/synapse/products/695/mw/6737.7ec91e479ef65c194b6c.js;SHA256=22D359EB7EDA08A9D2F4766EB6898A875A5F009EDB666BA4CD5A0425491AD1DA",
                "https://apps.razer.com/synapse/products/695/mw/main.27df2e7ddab0b6bc34cc.js;SHA256=A12CD7047CC1CF093DBB458D703BA4D18CFE77110F47391B180EA5B33BA5E379",
                "https://apps.razer.com/synapse/products/695/mw/rzDevice25.60bfcef58690c8bb715d.js;SHA256=2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491",
                "https://apps.razer.com/synapse/products/695/ui/static/js/main.65ed4ee5.js;SHA256=74ECA71816B57B4C9BE3F83D3E5D894F95A2050D88FB2DF4B63A5AFFF5271612",
                "https://apps.razer.com/synapse/products/695/mw/rzDevice25LedMatrix.76e9ad52d76940bbf7be.js;SHA256=9F2DE3A4CD127BF010E4AAD81C02A1D0D207B6F5CB7334D7F3822177A5B8A923",
                "https://apps.razer.com/synapse/products/695/mw/rzDevice25LedMatrixBlade.29566ed5280fbcc73b83.js;SHA256=8DF7DC371EECE383F791C206D7E44CBFB0EC98A2500337F634DF3A4A5C39EB93",
                "https://apps.razer.com/synapse/products/695/mw/rzDevice25LedMatrixBlade2.d7fc499124d26b637274.js;SHA256=EC3F8BCA0656F5BBB0872358ACAC387F7A2DFED42C9F5636D651DF1E85F1FF28",
                "https://apps.razer.com/synapse/products/695/mw/rzDevice25LedMatrixBladeWattage.8339706f4c96d35087e4.js;SHA256=BF2C42A139F3AC092A7DF913EBEC56684EE14C07241C6B56A0B276E54B3AD0F5"],
            696 => [
                "https://apps.razer.com/synapse/products/696/mw/6737.2463f7361e4d145671a2.js;SHA256=4EF6AFB6402DD50CD6DE07CA96E191F475797903507C82CAE194F0AF63E32ECC",
                "https://apps.razer.com/synapse/products/696/mw/main.ded18de8aee09a8f8238.js;SHA256=1A9CC3E1E8E28BB827AD17A720A03E6C62BA7A37F27774E8FA1B1B8E827E281F",
                "https://apps.razer.com/synapse/products/696/mw/rzDevice25.6339454cb5b2fe587300.js;SHA256=8780AA380D11665A2D551174D162C7278404465772219D0530A29B09E297BC81",
                "https://apps.razer.com/synapse/products/696/ui/static/js/main.cb24c0fc.js;SHA256=C882F2130FC41DC73EDA31A141BA82F8786B84F45992C490CC0DC5AC3EC28DCD",
                "https://apps.razer.com/synapse/products/696/mw/rzDevice25LedMatrix.9b4ab2e4321a6a556e6f.js;SHA256=8459713BCE0AA2D8F34A5A7C8382AA56DB65384570C12C02ACDBDD554FED40D6",
                "https://apps.razer.com/synapse/products/696/mw/rzDevice25LedMatrixBlade.d8b6805b3fb6452e52ce.js;SHA256=2F26EA7DA86C0504119A4C15C47430048E4C9A581BEB52F85A0847DB264FE4EA",
                "https://apps.razer.com/synapse/products/696/mw/rzDevice25LedMatrixBlade2.4a271d921402284f66f0.js;SHA256=2CF06F137FDC7AFD550D1D6CC8229A37C05CE96D9A9F4223F90E02BC456FF0E5",
                "https://apps.razer.com/synapse/products/696/mw/rzDevice25LedMatrixBladeWattage.522f5c0abf239227310d.js;SHA256=F99EAF5D64598CA475E5700602243520790C8E8E2F7ADCA4C78C85584FF14886"],
            711 => [
                "https://apps.razer.com/synapse/products/711/mw/6737.c63928b0e4fd1b7673b4.js;SHA256=3EF16C0BEB5E8399CDEE51E9995A717E029CDF2125324735440B68A34D2D7B88",
                "https://apps.razer.com/synapse/products/711/mw/main.1f8e0c604f19af3ea24c.js;SHA256=C8C0C794D6D01867CD9C6020869808428B81CB98AE297E46EC700F3B935BFF7E",
                "https://apps.razer.com/synapse/products/711/mw/rzDevice25.c8da1ead63e6cf002d32.js;SHA256=02C55B78B3EABDCC104D725C64C94CADE500FA78D29713ACE5FEF520D243EBB9",
                "https://apps.razer.com/synapse/products/711/ui/static/js/892.12b29c32.chunk.js;SHA256=FE1298AD9FAAE66301E76B439311F19393E4A46E1F8C5EB42B65350DA88F9100",
                "https://apps.razer.com/synapse/products/711/ui/static/js/main.a649ed49.js;SHA256=4B42EF4995470865B7D02587B85724247B886BDF2376EB6A78DA38BB9B6CBF99",
                "https://apps.razer.com/synapse/products/711/mw/rzDevice25LedMatrix.69e3b36effe3a85ab0de.js;SHA256=65C1FD97AD41460B8AFE33B08C0F8051D990CAF15DCF40D23930583DBC85664F",
                "https://apps.razer.com/synapse/products/711/mw/rzDevice25LedMatrixBlade.1ac7639291bd0bca2295.js;SHA256=CFD5529379EDE7C7EE0AA9C82A2726840DBB5B0B718C45024D8B7AD7486B7B85",
                "https://apps.razer.com/synapse/products/711/mw/rzDevice25LedMatrixBlade2.d678cb5b99e6382aebe0.js;SHA256=62356152B59E6D172AF8D07677534EBA0BF9DBFEBA973AFE51BEC8437D031FD8",
                "https://apps.razer.com/synapse/products/711/mw/rzDevice25LedMatrixBladeWattage.eb36277c35277a49ce09.js;SHA256=D48002486906EFA165DBC5219F37D810658010EDB5C7B9E61D945EE36A361E97"],
            _ => [],
        };

    internal static IHostIdentitySource CreateIdentity(
        ExperimentalBladeModel model,
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform) =>
        new WindowsHostIdentitySource(registry, platform,
            new ExpectedHostIdentity("Razer", model.ProductName, model.ModelNumber,
                Array.Empty<string>(), FirmwareAdmissionScope.ModelScoped));

    internal static ExperimentalBladeModel? FindCurrentHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var registry = new WindowsRegistryValueSource();
        var platform = new RuntimeWindowsPlatformEvidenceSource(registry);
        return All.FirstOrDefault(model =>
            CreateIdentity(model, registry, platform).ReadExactIdentity().Evidence is not null);
    }
}

internal enum ExperimentalRecoveryScope
{
    All,
    ThermalOnly,
    LightingFieldsOnly,
}

internal sealed class ExperimentalFeaturesPolicy(ExperimentalBladeModel model)
{
    private readonly object sync = new();
    private readonly LinkedList<ExperimentalControlObservation> observations = new();
    private readonly Dictionary<string, (string Outcome, string? Confirmed, string? Failure)> lastQueries = new(StringComparer.Ordinal);
    private LinkedListNode<ExperimentalControlObservation>? uniformFrameSuccess;
    private long uniformFrameSuccessCount;
    private LinkedListNode<ExperimentalControlObservation>? animationFrameSuccess;
    private long animationFrameSuccessCount;
    private System.Collections.Immutable.ImmutableDictionary<PerformancePreset, FanSpeedRange> confirmedFanRanges =
        System.Collections.Immutable.ImmutableDictionary<PerformancePreset, FanSpeedRange>.Empty;
    private int recoveryRequired;
    private ExperimentalRecoveryScope recoveryScope = ExperimentalRecoveryScope.All;

    // Observed capability metadata only; never a saved fan target or write admission.
    internal IReadOnlyDictionary<PerformancePreset, FanSpeedRange> ConfirmedFanRanges => Volatile.Read(ref confirmedFanRanges);

    internal void ConfirmFanRange(PerformancePreset preset, FanSpeedRange range) =>
        System.Collections.Immutable.ImmutableInterlocked.AddOrUpdate(ref confirmedFanRanges, preset, range, (_, _) => range);

    internal bool IsPaused => Volatile.Read(ref recoveryRequired) != 0;

    internal bool WritesAllowed => !IsPaused;

    internal ExperimentalRecoveryScope RecoveryScope
    {
        get
        {
            lock (sync)
            {
                return recoveryScope;
            }
        }
    }

    internal void RequireRecovery(ExperimentalRecoveryScope scope = ExperimentalRecoveryScope.All)
    {
        lock (sync)
        {
            // A known field-only lighting operation needs no effect baseline.
            // Different obligations combine to All; no later fault can narrow
            // an existing battery, effect or unknown submission obligation.
            ExperimentalRecoveryScope requested = scope is ExperimentalRecoveryScope.ThermalOnly
                or ExperimentalRecoveryScope.LightingFieldsOnly ? scope : ExperimentalRecoveryScope.All;
            recoveryScope = !IsPaused ? requested
                : recoveryScope == requested ? recoveryScope : ExperimentalRecoveryScope.All;
            Volatile.Write(ref recoveryRequired, 1);
        }
    }

    // The caller owns the provider hardware gate and has reconciled fresh readback.
    internal void CompleteRecovery()
    {
        lock (sync)
        {
            recoveryScope = ExperimentalRecoveryScope.All;
            Volatile.Write(ref recoveryRequired, 0);
        }
    }

    internal bool TryCompleteRecovery(ExperimentalRecoveryScope observedScope, Func<bool> acceptObservedState)
    {
        lock (sync)
        {
            // Keep runtime acceptance and pause release together. A widened
            // obligation cannot be discharged by an earlier partial observation.
            if (!IsPaused || recoveryScope != observedScope || !acceptObservedState()
                || recoveryScope != observedScope)
            {
                return false;
            }
            CompleteRecovery();
            return true;
        }
    }

    internal void Record(string operation, string stage, string outcome, bool submitted = false,
        string? requested = null, string? confirmed = null, string? failure = null)
    {
        lock (sync)
        {
            if (stage == "Query")
            {
                var current = (outcome, confirmed, failure);
                if (lastQueries.TryGetValue(operation, out var previous) && previous == current)
                {
                    return;
                }
                lastQueries[operation] = current;
            }
            Append(new(DateTimeOffset.UtcNow, operation, stage, outcome,
                submitted, requested, confirmed, failure));
        }
    }

    internal void RecordUniformFrameApplied(RgbColor color)
    {
        lock (sync)
        {
            uniformFrameSuccessCount++;
            if (uniformFrameSuccess?.List is not null)
            {
                observations.Remove(uniformFrameSuccess);
            }
            uniformFrameSuccess = Append(new(
                DateTimeOffset.UtcNow,
                "KeyboardUniformFrame",
                "Readback",
                "Unavailable",
                true,
                $"LastRGB={color.Red:X2}{color.Green:X2}{color.Blue:X2}",
                $"AppliedWithoutColorReadback;Frames={uniformFrameSuccessCount}",
                "FrameColorReadbackUnavailable"));
        }
    }

    internal void RecordAnimationFrameApplied(LightingEffect effect)
    {
        lock (sync)
        {
            animationFrameSuccessCount++;
            if (animationFrameSuccess?.List is not null)
            {
                observations.Remove(animationFrameSuccess);
            }
            animationFrameSuccess = Append(new(DateTimeOffset.UtcNow,
                "KeyboardAnimationFrame", "Readback", "Unavailable", true,
                effect.ToString(), $"AppliedWithoutColorReadback;Frames={animationFrameSuccessCount}",
                "FrameColorReadbackUnavailable"));
        }
    }

    private LinkedListNode<ExperimentalControlObservation> Append(
        ExperimentalControlObservation observation)
    {
        while (observations.Count >= DiagnosticExperimentalSupport.MaxObservations)
        {
            LinkedListNode<ExperimentalControlObservation>? removed = observations.First;
            if (removed is null)
            {
                break;
            }
            observations.RemoveFirst();
            if (ReferenceEquals(removed, uniformFrameSuccess))
            {
                uniformFrameSuccess = null;
            }
        }
        return observations.AddLast(observation);
    }

    internal DiagnosticExperimentalSupport Snapshot()
    {
        lock (sync)
        {
            return new(model.ModelNumber, model.EvidenceSource, true, IsPaused, observations.ToArray())
            {
                FeatureEvidence = ExperimentalBladeModels.FeatureEvidenceFor(model),
            };
        }
    }
}
