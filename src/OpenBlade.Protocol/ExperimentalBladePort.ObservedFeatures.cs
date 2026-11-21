using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// Synapse features that OpenBlade represents as ObservedOnly. Each one is
// applied by Synapse through an unreviewed native vendor DLL, so no OpenBlade
// write path exists (AGENTS.md rule 6). See docs/experimental-blade-support.md and
// evidence/source-reviewed-blades/2026-11-20-observed-features-and-local-dimming.json.
internal sealed partial class ExperimentalBladePort
{
    // hasAMDOverclock in product 669 (RZ09-0482), 694 (RZ09-0508) and 709
    // (RZ09-0530) mw main. activePowerPreset CPUPowerLimit applies in Custom
    // mode only; the UI range is 45-95 W in 1 W steps (RzAMDOverClock).
    internal static bool SupportsObservedAmdCpuPowerLimit(ExperimentalBladeModel model) =>
        model.ProductId is 0x029D or 0x02B6 or 0x02C5;

    // hasIntelCPUOverclocking in product 671 (RZ09-0483), 672 (RZ09-0484), 695
    // (RZ09-0510), 696 (RZ09-0509) and 711 (RZ09-0529) mw main, each with the
    // default profile PL2 160 W enabled, PL1 120 W and tau 56 s (RzIntelOverClock
    // control IDs 47 PL2, 48 PL1, 49 PL2 enable, 66 tau).
    internal static bool SupportsObservedIntelCpuPowerLimit(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x02A0 or 0x02B7 or 0x02B8 or 0x02C7;

    // Products 582 (RZ09-0300), 616 (RZ09-0351) and 597 (RZ09-0328) use the
    // rzDevice25LedMatrixBladeWattage setOverClockingMode: Gaming mode reads the
    // 0D/83 GPU and VRAM fields and passes them to native SetGPUValue; other modes
    // send 0,0. The same 0D/83 bytes also carry fan-table fields.
    internal static bool SupportsObservedFirmwareGamingGpuOverclock(ExperimentalBladeModel model) =>
        model.ProductId is 0x0246 or 0x0268 or 0x0255;

    // UI ranges from 695 main.65ed4ee5.js (char 6904126), 696 main.cb24c0fc.js
    // (char 7174591) and 711 chunk 892.12b29c32.js (char 113285): PL2 1-200 W
    // with an enable box, PL1 1-190 W, both in 0.125 W steps, and the tau list.
    internal static readonly IntelCpuPowerLimitCapabilities SynapseIntelCpuPowerLimit = new(
        LongTermPowerMilliwatts: new DeviceNumericRange(1_000, 190_000, 125),
        ShortTermPowerMilliwatts: new DeviceNumericRange(1_000, 200_000, 125),
        TimeWindowSeconds: Array.AsReadOnly(new[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128,
        }),
        DefaultLongTermPowerMilliwatts: 120_000,
        DefaultShortTermPowerMilliwatts: 160_000,
        DefaultTimeWindowSeconds: 56,
        Access: DeviceFeatureAccess.ObservedOnly)
    {
        SupportsShortTermPowerEnableDisable = true,
    };

    private const string SynapseProducts = "https://apps.razer.com/synapse/products/";
    internal const string ObservedFeaturesReview = "evidence/source-reviewed-blades/2026-11-20-observed-features-and-local-dimming.json";

    // Feature evidence for the ObservedOnly features above and the mini-LED Clear.
    internal static IReadOnlyList<ExperimentalFeatureEvidence> ObservedFeatureEvidence(ExperimentalBladeModel model)
    {
        List<ExperimentalFeatureEvidence> features = [];
        if (SupportsObservedAmdCpuPowerLimit(model))
        {
            features.Add(new("CpuPowerLimit", "SynapseSourceObservedOnlyNativeVendorWrite", [
                model.ProductId switch
                {
                    0x029D => SynapseProducts + "669/mw/main.8e09720cb94da405eead.js;SHA256=EEB8A171E77D1E066655D7FC997E4015B420B0F936F2464A27A87FDAFB5AC302",
                    0x02B6 => SynapseProducts + "694/mw/main.1b1b68e07824e5af9833.js;SHA256=F1AEEAFF4BBDB631B4E66BF8B2B6415FB0EADDF24343A146787B091F89AA7BDC",
                    _ => SynapseProducts + "709/mw/main.bb0dd1873a58a987c63a.js;SHA256=CC8E0EFD2B37F1ED5AF6C8CC8C3AB1327B9502927DC05F20EE7465486AB006F1",
                },
                ObservedFeaturesReview]));
        }
        if (SupportsObservedIntelCpuPowerLimit(model))
        {
            features.Add(new("IntelCpuPowerLimits", model.ProductId is 0x029F or 0x02A0
                    ? "SynapseSourceObservedOnlyNativeVendorWriteUiRangeExtrapolated"
                    : "SynapseSourceObservedOnlyNativeVendorWrite",
                model.ProductId switch
                {
                    0x029F => [SynapseProducts + "671/mw/main.2ceb8f5607c0e7a5901a.js;SHA256=CACF8758BF8B4DF35B7D51536830149ED428960AFEDF08CC81367E75520551D1", ObservedFeaturesReview],
                    0x02A0 => [SynapseProducts + "672/mw/main.5cd0d12a2bff4b5edcf2.js;SHA256=61832E9F7C55E727D929DE103136D0684919020D7BA833FCDF16C9061667DAA4", ObservedFeaturesReview],
                    0x02B7 => [SynapseProducts + "695/mw/main.27df2e7ddab0b6bc34cc.js;SHA256=A12CD7047CC1CF093DBB458D703BA4D18CFE77110F47391B180EA5B33BA5E379",
                        SynapseProducts + "695/mw/6737.7ec91e479ef65c194b6c.js;SHA256=22D359EB7EDA08A9D2F4766EB6898A875A5F009EDB666BA4CD5A0425491AD1DA",
                        SynapseProducts + "695/ui/static/js/main.65ed4ee5.js;SHA256=74ECA71816B57B4C9BE3F83D3E5D894F95A2050D88FB2DF4B63A5AFFF5271612", ObservedFeaturesReview],
                    0x02B8 => [SynapseProducts + "696/mw/main.ded18de8aee09a8f8238.js;SHA256=1A9CC3E1E8E28BB827AD17A720A03E6C62BA7A37F27774E8FA1B1B8E827E281F",
                        SynapseProducts + "696/ui/static/js/main.cb24c0fc.js;SHA256=C882F2130FC41DC73EDA31A141BA82F8786B84F45992C490CC0DC5AC3EC28DCD", ObservedFeaturesReview],
                    _ => [SynapseProducts + "711/mw/main.1f8e0c604f19af3ea24c.js;SHA256=C8C0C794D6D01867CD9C6020869808428B81CB98AE297E46EC700F3B935BFF7E",
                        SynapseProducts + "711/ui/static/js/892.12b29c32.chunk.js;SHA256=FE1298AD9FAAE66301E76B439311F19393E4A46E1F8C5EB42B65350DA88F9100", ObservedFeaturesReview],
                }));
        }
        if (SupportsObservedFirmwareGamingGpuOverclock(model))
        {
            features.Add(new("FirmwareGamingGpuOverclock", "SynapseSourceObservedOnlyNativeVendorWrite",
                model.ProductId switch
                {
                    0x0246 => [SynapseProducts + "582/mw/rzDevice25LedMatrixBladeWattage.45c847b37f79a031cbaf.js;SHA256=71D91DFF87CB617384535C5DEC4643A2CC551C3CDE63EE691A29D7282FF96AB2",
                        SynapseProducts + "582/mw/rzDevice25.56d11fe9830f93920735.js;SHA256=A5F75408C2D859ACF0F3418B950989271E98DFEE5056DD17DCA6BA5557FDD4C6", ObservedFeaturesReview],
                    0x0268 => [SynapseProducts + "616/mw/main.f9ef043eaf3feceba719.js;SHA256=ABC074EB488E0848E4751D8417D6B3EF3BA02FAC06C9C98ED32B5E1E1350B2C7",
                        SynapseProducts + "616/mw/rzDevice25.9c9299b0f9660c882e16.js;SHA256=330835150F88BC505B181EAC4828506750CD7365F9F4CB242437CF3CFC133A24", ObservedFeaturesReview],
                    _ => [SynapseProducts + "597/mw/6737.6da70458ec9e64b00f37.js;SHA256=C6E6E76DFB61F960913E22B864A31273280547E21E1462DE3138F096D2DB56EA",
                        SynapseProducts + "597/mw/rzDevice25.ae57bbfa911460c03510.js;SHA256=2F41299C36E60E65D0BFCCE1E666FF67F0FC07F21F4523BED0A4E8CA5AC6043D", ObservedFeaturesReview],
                }));
        }
        if (SupportsLocalDimmingClear(model))
        {
            features.Add(new("LocalDimmingClearOnMiniLedPanels", "SynapseSourceBackedWithoutPhysicalValidation",
                model.ProductId == 0x02B7
                    ? [SynapseProducts + "695/mw/rzDevice25.60bfcef58690c8bb715d.js;SHA256=2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491",
                        SynapseProducts + "695/ui/static/js/main.65ed4ee5.js;SHA256=74ECA71816B57B4C9BE3F83D3E5D894F95A2050D88FB2DF4B63A5AFFF5271612", ObservedFeaturesReview]
                    : [SynapseProducts + "696/mw/rzDevice25.6339454cb5b2fe587300.js;SHA256=8780AA380D11665A2D551174D162C7278404465772219D0530A29B09E297BC81",
                        SynapseProducts + "696/ui/static/js/main.cb24c0fc.js;SHA256=C882F2130FC41DC73EDA31A141BA82F8786B84F45992C490CC0DC5AC3EC28DCD", ObservedFeaturesReview]));
        }
        if (model.ProductId == 0x02C5)
        {
            features.Add(new("LocalDimmingFollowsWindowsHdr", "SynapseSourceObservedOnly", [
                SynapseProducts + "709/mw/main.bb0dd1873a58a987c63a.js;SHA256=CC8E0EFD2B37F1ED5AF6C8CC8C3AB1327B9502927DC05F20EE7465486AB006F1",
                SynapseProducts + "709/mw/6737.d34a3f9a638030f35c6a.js;SHA256=4758FB7E2D8296564FB9CE971F6DCE3D580ED5407EEF07A69E1C362A04FF28C0",
                ObservedFeaturesReview]));
        }
        return features;
    }
}
