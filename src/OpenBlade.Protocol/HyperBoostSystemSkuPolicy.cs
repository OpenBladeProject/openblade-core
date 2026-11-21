namespace OpenBlade.Protocol;

internal enum HyperBoostSystemSkuAdmission
{
    // The exact model's vendor source does not run the SKU recheck.
    NotApplicable,
    Admitted,
    // Synapse sets shouldDisableHyperboost for this SKU.
    DisabledByVendorSkuPolicy,
    // The SKU was missing or had no tenth character. Synapse's charAt(9) then
    // returns "", which matches no excluded character, so entry stays admitted.
    SkuUnavailable,
}

/// <summary>
/// The vendor HyperBoost SKU recheck. It declines new HyperBoost entries only;
/// exit, restoration and journal recovery never depend on it. It is not host
/// admission and never rejects the laptop or any other control.
/// </summary>
internal static class HyperBoostSystemSkuPolicy
{
    internal const int VendorSkuCharacterIndex = 9;

    internal const string EntryRestrictedMessage =
        "Synapse doesn't offer HyperBoost on this laptop configuration, so OpenBlade doesn't start it. You can still turn HyperBoost off.";

    // Product 671 (029F) main.2ceb8f5607c0e7a5901a.js (SHA-256
    // CACF8758BF8B4DF35B7D51536830149ED428960AFEDF08CC81367E75520551D1) and
    // product 695 (02B7) main.27df2e7ddab0b6bc34cc.js (SHA-256
    // A12CD7047CC1CF093DBB458D703BA4D18CFE77110F47391B180EA5B33BA5E379) set
    // needHyperboostRecheck:"yes" (useFeature at bytes 65,855 and 163,099). Their
    // policies then set shouldDisableHyperboost when systemSKU.charAt(9) is R or S
    // (TypeR, TypeS), S, or 5 or 6 (Type5, Type6): 671 6737.e123a2b3f0516116025b.js
    // (SHA-256 54D3A9BBEC83568B02342AA54FF47A94EE55A48ECA85B5D9F28D60D01A0B1A9A)
    // charAt bytes 151,548-151,787, assignments 146,227 and 132,937; and 695
    // 6737.7ec91e479ef65c194b6c.js (SHA-256
    // 22D359EB7EDA08A9D2F4766EB6898A875A5F009EDB666BA4CD5A0425491AD1DA) predicate
    // byte 1,255,621, assignments 1,256,337 and 1,278,858. A set flag hides the
    // mode and skips the pad offer (695 policy bytes 1,793,192 and 1,795,779).
    // 695 main's systemSKU getter (byte 101,870) returns the host's SKU, or "false"
    // outside its two hosts. charAt(9) of any value shorter than ten characters is
    // "", which matches no excluded character, so a missing or short SKU admits
    // HyperBoost. OpenBlade matches that.
    // The meaning of SKU character 9 is not established; OpenBlade follows the
    // predicate as written.
    // Product 669 (029D) policy 6737.439ff1a12463933a6346.js has only a 5/6
    // predicate (byte 1,220,019) behind the same needHyperboostRecheck guard
    // (byte 1,214,688), but its main.8e09720cb94da405eead.js never sets that
    // feature, so the recheck never runs and 029D stays ungated.
    internal static bool AppliesTo(ushort productId) => productId is 0x029F or 0x02B7;

    internal static bool DisablesHyperBoost(char vendorSkuCharacter) =>
        vendorSkuCharacter is 'R' or 'S' or '5' or '6';

    internal static HyperBoostSystemSkuAdmission Evaluate(ushort productId, string? systemSku)
    {
        if (!AppliesTo(productId))
        {
            return HyperBoostSystemSkuAdmission.NotApplicable;
        }
        if (systemSku is null || systemSku.Length <= VendorSkuCharacterIndex)
        {
            return HyperBoostSystemSkuAdmission.SkuUnavailable;
        }
        return DisablesHyperBoost(systemSku[VendorSkuCharacterIndex])
            ? HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy
            : HyperBoostSystemSkuAdmission.Admitted;
    }

    // Only a positively read excluded character declines entry, as in Synapse.
    internal static bool AllowsEntry(HyperBoostSystemSkuAdmission admission) =>
        admission != HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy;

    // Diagnostics record only the evaluated character, never the full SKU.
    internal static string DescribeForDiagnostics(HyperBoostSystemSkuAdmission admission, string? systemSku) =>
        admission switch
        {
            HyperBoostSystemSkuAdmission.SkuUnavailable => "SkuCharacter9=Unavailable",
            HyperBoostSystemSkuAdmission.NotApplicable => "SkuRecheck=NotDeclared",
            _ => $"SkuCharacter9={systemSku![VendorSkuCharacterIndex]}",
        };
}
