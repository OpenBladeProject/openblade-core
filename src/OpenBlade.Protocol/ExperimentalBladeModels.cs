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
    bool SupportsBalancedDc = true)
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
                new(thermal.SupportsCpuBoost ? "PerformanceAndCustomCpu0To3Gpu0To2" : "PerformanceAndCustomLevels0To2",
                    "SynapseSourceBackedWithoutPhysicalValidation", sources),
                new("AutomaticFanHandback", "SynapseSourceBackedWithoutPhysicalValidation", sources),
                new("FixedAndCurveFanWrites", "NotEstablishedDynamicFanRangesRequireReview", sources),
                new("ManualBaselinePerformanceChanges", "BlockedUntilFanTargetPreservationIsAdmitted", sources),
                new("ChargeLimits50To80Step5AndOff", model.SupportsBatteryProtection
                    ? "SynapseSourceBackedWithoutPhysicalValidation" : "NotEstablished", sources),
                new("MaximumFans", thermal.SupportsMaximumFans ? "SynapseSourceBackedWithoutPhysicalValidation" : "NotEstablished", sources),
                new("AdapterPowerClassification", "SynapseSourceBacked", sources),
                new("LightingAndHyperBoost", "NotEstablished", sources),
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
        return model.AdapterPowerEvidenceSource is { } source
            ? [.. features, new("AdapterPowerClassification", "SynapseSourceBacked", [source])]
            : features;
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
    ]);

    private static ExperimentalBladeModel CreateSourceReviewed(string number, string name, ushort product,
        string mainFile, string commandFile, string commandHash, bool maximumFans,
        bool batteryPresets = false, bool maximumPower = false, bool cpuBoost = false,
        bool turbo = true, bool silent = true, bool balancedDc = true, bool batteryProtection = true)
    {
        string root = $"https://apps.razer.com/synapse/products/{product}/mw/";
        return new(number, name, product, batteryProtection, root + mainFile, root + mainFile,
            new(root + commandFile + ";SHA256=" + commandHash, maximumFans, cpuBoost, batteryPresets, maximumPower,
                SourceReviewedEvidence(product), turbo, silent, balancedDc));
    }

    private static System.Collections.Immutable.ImmutableArray<string> SourceReviewedEvidence(ushort product) =>
        product switch
        {
            624 => [
                "https://apps.razer.com/synapse/products/624/mw/6737.6b47e5cb8059a1bdacfb.js;SHA256=C36EFB8BD1F15DC65F8C235895EFA5F20A59D71D45BC6D22BF92D88C9C8D79F7",
                "https://apps.razer.com/synapse/products/624/mw/main.c661207b6abe5eb319cb.js;SHA256=50DFF3488639F22BD3890596A047378D14CB52334A49EDDCEDDAFC7EAF0669C2",
                "https://apps.razer.com/synapse/products/624/mw/rzDevice25.1672779ed3e58b9415ca.js;SHA256=F696504C78559A2E04E503F8CF7B069D87F1298BFB767677912CD649EEB49456",
                "https://apps.razer.com/synapse/products/624/ui/static/js/main.2709843a.js;SHA256=48359BAFE13C56156017A81A72E82CEDB3F44BAD5A52BB921D4FBE96E1F1A1FE"],
            652 => [
                "https://apps.razer.com/synapse/products/652/mw/6737.4b526d5a88f50676de59.js;SHA256=B1A181C2F4A95C1871A5AEEDC5F820DBC991A61A067CB060BAC9ABEBF04EE488",
                "https://apps.razer.com/synapse/products/652/mw/main.825dec01b9aefa0b84ac.js;SHA256=77AD6A8F51278C2950D07AAB3CA4E98EC67001527067F4118F50AF00954EABA1",
                "https://apps.razer.com/synapse/products/652/mw/rzDevice25.ceef38d32121535173d9.js;SHA256=D94B40BB4B1AEDD17C0028A625C28D00D8BC14B26361CBF25133A3CE4E5B0C67",
                "https://apps.razer.com/synapse/products/652/ui/static/js/main.ca1e43bb.js;SHA256=9C640FF7F3359DFB8F8D7AA005DB01336F5C05A4EFB25C219550BA6D05A6C407"],
            650 => [
                "https://apps.razer.com/synapse/products/650/mw/6737.1cc98c1e1650ffce7912.js;SHA256=8B31CFB0033A9C75F5E6E69D914A3A36BAE0A4944886B5CC6D076329434D1DF9",
                "https://apps.razer.com/synapse/products/650/mw/main.3650e97f35689328d442.js;SHA256=270D5D4E4AF9674D2219EFBE609294DC46E1BAF4E08A14AF32306D4495CB5A98",
                "https://apps.razer.com/synapse/products/650/mw/rzDevice25.33bbbe9e1d2d72bdf1a1.js;SHA256=CEF5D482E96F2833C84998E2FE998D0A41AA7CA4483E4E69156424F76475D513",
                "https://apps.razer.com/synapse/products/650/ui/static/js/main.90e358d5.js;SHA256=06C52972C023B65B11DDF1C379671526E96C68B46105FC3EDCC714F0EF4944B0"],
            651 => [
                "https://apps.razer.com/synapse/products/651/mw/6737.003892ae34f5de0292c0.js;SHA256=E8A2CFF2F19A59B8F425E7EC70BFF5873551479560005435E1B04B34F9CC7454",
                "https://apps.razer.com/synapse/products/651/mw/main.8378c0c79039a3f2ba23.js;SHA256=F227CBA8959DFEE0B898077D4CC77651066B7BFFF8CAA20910E9B3946E026AC4",
                "https://apps.razer.com/synapse/products/651/mw/rzDevice25.7ffcfe7d1ddb6103ac44.js;SHA256=A9442C86AB7D8F0549E18016324462A52A040E5578410F296BDF72884A383962",
                "https://apps.razer.com/synapse/products/651/ui/static/js/main.8fce5f78.js;SHA256=FF4ACE62F536DAD8E14A674177638FAC74ED462C74FECC7A42D66B2F3616C254"],
            694 => [
                "https://apps.razer.com/synapse/products/694/mw/6737.63910b6756fd6d249dc6.js;SHA256=488D72368F0EBFB2A4DD80BE68E8C2BAB8020CBBCC65CA786757FEA79507B997",
                "https://apps.razer.com/synapse/products/694/mw/main.1b1b68e07824e5af9833.js;SHA256=F1AEEAFF4BBDB631B4E66BF8B2B6415FB0EADDF24343A146787B091F89AA7BDC",
                "https://apps.razer.com/synapse/products/694/mw/rzDevice25.06135650b7453937e0e8.js;SHA256=0E5BD5C21DD610E5D889F608B2EBB1D80BFC7CA90237FBBC3A292ADBAF06EF90",
                "https://apps.razer.com/synapse/products/694/ui/static/js/main.415cc878.js;SHA256=C22750AAD6E54EE86E04B3D7F7D7DA520636293C209734358FC26A744D84F6B1"],
            695 => [
                "https://apps.razer.com/synapse/products/695/mw/6737.7ec91e479ef65c194b6c.js;SHA256=22D359EB7EDA08A9D2F4766EB6898A875A5F009EDB666BA4CD5A0425491AD1DA",
                "https://apps.razer.com/synapse/products/695/mw/main.27df2e7ddab0b6bc34cc.js;SHA256=A12CD7047CC1CF093DBB458D703BA4D18CFE77110F47391B180EA5B33BA5E379",
                "https://apps.razer.com/synapse/products/695/mw/rzDevice25.60bfcef58690c8bb715d.js;SHA256=2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491",
                "https://apps.razer.com/synapse/products/695/ui/static/js/main.65ed4ee5.js;SHA256=74ECA71816B57B4C9BE3F83D3E5D894F95A2050D88FB2DF4B63A5AFFF5271612"],
            696 => [
                "https://apps.razer.com/synapse/products/696/mw/6737.2463f7361e4d145671a2.js;SHA256=4EF6AFB6402DD50CD6DE07CA96E191F475797903507C82CAE194F0AF63E32ECC",
                "https://apps.razer.com/synapse/products/696/mw/main.ded18de8aee09a8f8238.js;SHA256=1A9CC3E1E8E28BB827AD17A720A03E6C62BA7A37F27774E8FA1B1B8E827E281F",
                "https://apps.razer.com/synapse/products/696/mw/rzDevice25.6339454cb5b2fe587300.js;SHA256=8780AA380D11665A2D551174D162C7278404465772219D0530A29B09E297BC81",
                "https://apps.razer.com/synapse/products/696/ui/static/js/main.cb24c0fc.js;SHA256=C882F2130FC41DC73EDA31A141BA82F8786B84F45992C490CC0DC5AC3EC28DCD"],
            711 => [
                "https://apps.razer.com/synapse/products/711/mw/6737.c63928b0e4fd1b7673b4.js;SHA256=3EF16C0BEB5E8399CDEE51E9995A717E029CDF2125324735440B68A34D2D7B88",
                "https://apps.razer.com/synapse/products/711/mw/main.1f8e0c604f19af3ea24c.js;SHA256=C8C0C794D6D01867CD9C6020869808428B81CB98AE297E46EC700F3B935BFF7E",
                "https://apps.razer.com/synapse/products/711/mw/rzDevice25.c8da1ead63e6cf002d32.js;SHA256=02C55B78B3EABDCC104D725C64C94CADE500FA78D29713ACE5FEF520D243EBB9",
                "https://apps.razer.com/synapse/products/711/ui/static/js/892.12b29c32.chunk.js;SHA256=FE1298AD9FAAE66301E76B439311F19393E4A46E1F8C5EB42B65350DA88F9100",
                "https://apps.razer.com/synapse/products/711/ui/static/js/main.a649ed49.js;SHA256=4B42EF4995470865B7D02587B85724247B886BDF2376EB6A78DA38BB9B6CBF99"],
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

internal sealed class ExperimentalFeaturesPolicy(ExperimentalBladeModel model)
{
    private readonly object sync = new();
    private readonly LinkedList<ExperimentalControlObservation> observations = new();
    private readonly Dictionary<string, (string Outcome, string? Confirmed, string? Failure)> lastQueries = new(StringComparer.Ordinal);
    private LinkedListNode<ExperimentalControlObservation>? uniformFrameSuccess;
    private long uniformFrameSuccessCount;
    private LinkedListNode<ExperimentalControlObservation>? animationFrameSuccess;
    private long animationFrameSuccessCount;
    private int recoveryRequired;

    internal bool IsPaused => Volatile.Read(ref recoveryRequired) != 0;

    internal bool WritesAllowed => !IsPaused;

    internal void RequireRecovery() => Volatile.Write(ref recoveryRequired, 1);

    // The caller owns the provider hardware gate and has reconciled fresh readback.
    internal void CompleteRecovery() => Volatile.Write(ref recoveryRequired, 0);

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
