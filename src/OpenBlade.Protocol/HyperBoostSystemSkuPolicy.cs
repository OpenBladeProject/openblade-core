namespace OpenBlade.Protocol;

internal enum HyperBoostSystemSkuAdmission
{
    // The exact model's vendor source does not run the SKU recheck.
    NotApplicable,
    Admitted,
    // Synapse sets shouldDisableHyperboost for this SKU.
    DisabledByVendorSkuPolicy,
    // The SKU could not be read, so the vendor predicate cannot be evaluated.
    SkuUnavailable,
}

/// <summary>
/// The vendor HyperBoost SKU recheck. This is a HyperBoost capability gate that
/// follows exact-model vendor policy; it is not host admission, and it never
/// rejects the laptop or any other control.
/// </summary>
internal static class HyperBoostSystemSkuPolicy
{
    internal const int VendorSkuCharacterIndex = 9;

    internal const string DisabledMessage =
        "Synapse turns HyperBoost off on this laptop configuration, so OpenBlade does too.";

    internal const string UnavailableMessage =
        "OpenBlade could not read this laptop's system SKU, which HyperBoost needs. Other controls are unaffected.";

    // Product 671 (029F) main.2ceb8f5607c0e7a5901a.js (SHA-256
    // CACF8758BF8B4DF35B7D51536830149ED428960AFEDF08CC81367E75520551D1) and
    // product 695 (02B7) main.27df2e7ddab0b6bc34cc.js (SHA-256
    // A12CD7047CC1CF093DBB458D703BA4D18CFE77110F47391B180EA5B33BA5E379) set
    // needHyperboostRecheck:"yes" (byte offsets 65,867 and 163,111). Their policies
    // then set shouldDisableHyperboost when systemSKU.charAt(9) is R or S (TypeR,
    // TypeS), S, or 5 or 6 (Type5, Type6): 671 6737.e123a2b3f0516116025b.js
    // (SHA-256 54D3A9BBEC83568B02342AA54FF47A94EE55A48ECA85B5D9F28D60D01A0B1A9A)
    // charAt bytes 151,548-151,787, assignment byte 146,232; and 695
    // 6737.7ec91e479ef65c194b6c.js (SHA-256
    // 22D359EB7EDA08A9D2F4766EB6898A875A5F009EDB666BA4CD5A0425491AD1DA) predicate
    // byte 1,255,621, assignments 1,256,342 and 1,278,863. A set flag hides the mode and skips the
    // pad offer (695 policy bytes 1,793,192 and 1,795,779). The meaning of SKU character 9 is not
    // established; OpenBlade follows the predicate as written.
    // Product 669 (029D) policy 6737.439ff1a12463933a6346.js has only a 5/6
    // predicate (byte 1,220,019), but its main.8e09720cb94da405eead.js never sets
    // needHyperboostRecheck, so the recheck never runs and 029D stays ungated.
    internal static bool AppliesTo(ushort productId) => productId is 0x029F or 0x02B7;

    internal static bool DisablesHyperBoost(char vendorSkuCharacter) =>
        vendorSkuCharacter is 'R' or 'S' or '5' or '6';

    internal static HyperBoostSystemSkuAdmission Evaluate(ushort productId, string? systemSku)
    {
        if (!AppliesTo(productId))
        {
            return HyperBoostSystemSkuAdmission.NotApplicable;
        }
        // Synapse's charAt(9) returns "" for a shorter string and admits it. A missing
        // or short value is not evidence about the vendor predicate, so HyperBoost
        // alone fails closed here.
        if (string.IsNullOrWhiteSpace(systemSku) || systemSku.Length <= VendorSkuCharacterIndex)
        {
            return HyperBoostSystemSkuAdmission.SkuUnavailable;
        }
        return DisablesHyperBoost(systemSku[VendorSkuCharacterIndex])
            ? HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy
            : HyperBoostSystemSkuAdmission.Admitted;
    }

    internal static bool AllowsWrites(HyperBoostSystemSkuAdmission admission) =>
        admission is HyperBoostSystemSkuAdmission.NotApplicable or HyperBoostSystemSkuAdmission.Admitted;

    // Diagnostics record only the evaluated character, never the full SKU.
    internal static string DescribeForDiagnostics(HyperBoostSystemSkuAdmission admission, string? systemSku) =>
        admission switch
        {
            HyperBoostSystemSkuAdmission.SkuUnavailable => "SkuCharacter9=Unavailable",
            HyperBoostSystemSkuAdmission.NotApplicable => "SkuRecheck=NotDeclared",
            _ => $"SkuCharacter9={systemSku![VendorSkuCharacterIndex]}",
        };
}
