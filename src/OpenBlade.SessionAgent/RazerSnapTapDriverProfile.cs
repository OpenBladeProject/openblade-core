namespace OpenBlade.SessionAgent;

/// <summary>Exact-model identity for the reviewed shared Razer hook contract.</summary>
internal sealed record RazerSnapTapDriverProfile(
    string ProductName,
    string ProductId,
    string DeviceSha256,
    bool AutomaticInstallationAvailable,
    bool PhysicallyValidated)
{
    internal string DeviceService => $"RzDev_{ProductId.ToLowerInvariant()}";
    internal string DeviceFileName => DeviceService + ".sys";
    internal string ControlInstancePrefix => $@"RZCONTROL\VID_1532&PID_{ProductId}&MI_00\";
    internal string ControlAncestorPrefix => $@"USB\VID_1532&PID_{ProductId}&MI_02\";
    internal string KeyboardInstancePrefix => $@"HID\VID_1532&PID_{ProductId}&MI_01&COL01\";
    internal string KeyboardAncestorPrefix => $@"USB\VID_1532&PID_{ProductId}&MI_01\";
}

internal static class RazerSnapTapDriverProfiles
{
    internal const string CommonSha256 =
        "64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59";

    // Each entry requires its own signed filter and topology review. Sharing
    // RzCommon alone does not establish a model's hook forwarding contract.
    // Pins and static callback review: docs/snap-tap-driver-review.md.
    internal static IReadOnlyList<RazerSnapTapDriverProfile> All { get; } = Array.AsReadOnly(
        new RazerSnapTapDriverProfile[]
        {
            new("Blade 16 - RZ09-0581", "02E0",
                "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787",
                AutomaticInstallationAvailable: true, PhysicallyValidated: true),
            new("Blade 16 - RZ09-0528", "02C6",
                "307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 16 - RZ09-0483", "029F",
                "97766397880C87B6C606E040C53EB3779A77EEA8CA62154C4C3C2C80FD401535",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 14 - RZ09-0482", "029D",
                "556049CFA85B817A899D6D3EF5AF42C6D45F0198CA68CE69908C6D9FFA60070D",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 14 - RZ09-0530", "02C5",
                "597C281383D4C7F07A789E88DF92E7814DC7F8118AC5631D3BF4FE8E5E1A87EB",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 15 Base Model (Early 2020) - RZ09-0328", "0255",
                "0533D4A3B83243959254E05BA45B7290B6FB6CF6CCCF4F71FF255EFF08BE55E2",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 18 - RZ09-0484", "02A0",
                "AD50A47698F0FA882B0A44F5CC898A1A8A1B8D9213BFDFF629CF49F63872D180",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
        });

    internal static RazerSnapTapDriverProfile? Find(
        string? biosManufacturer, string? biosProduct,
        string? systemManufacturer, string? systemProduct) =>
        string.Equals(biosManufacturer, "Razer", StringComparison.Ordinal)
        && string.Equals(systemManufacturer, "Razer", StringComparison.Ordinal)
        && string.Equals(biosProduct, systemProduct, StringComparison.Ordinal)
            ? All.SingleOrDefault(profile => string.Equals(
                profile.ProductName, biosProduct, StringComparison.Ordinal))
            : null;
}
