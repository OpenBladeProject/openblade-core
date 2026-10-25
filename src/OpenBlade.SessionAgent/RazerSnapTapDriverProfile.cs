namespace OpenBlade.SessionAgent;

/// <summary>Exact-model identity for the reviewed shared Razer hook contract.</summary>
internal sealed record RazerSnapTapDriverProfile(
    string ProductName,
    string ProductId,
    bool AutomaticInstallationAvailable,
    bool PhysicallyValidated)
{
    internal string DeviceService => $"RzDev_{ProductId.ToLowerInvariant()}";
    internal string DeviceFileName => DeviceService + ".sys";
    internal string ControlInstancePrefix => $@"RZCONTROL\VID_1532&PID_{ProductId}&MI_00\";
    internal string ControlAncestorPrefix => $@"USB\VID_1532&PID_{ProductId}&MI_02\";
    internal string KeyboardHardwareId => $@"HID\VID_1532&PID_{ProductId}&MI_01&COL01";
    internal string KeyboardInstancePrefix => KeyboardHardwareId + @"\";
    internal string KeyboardAncestorPrefix => $@"USB\VID_1532&PID_{ProductId}&MI_01\";
}

internal static class RazerSnapTapDriverProfiles
{
    // Each entry requires its own signed filter and topology review. Sharing
    // RzCommon alone does not establish a model's hook forwarding contract.
    // Source provenance and static callback review: docs/snap-tap-driver-review.md.
    internal static IReadOnlyList<RazerSnapTapDriverProfile> All { get; } = Array.AsReadOnly(
        new RazerSnapTapDriverProfile[]
        {
            new("Blade 16 - RZ09-0581", "02E0",
                AutomaticInstallationAvailable: true, PhysicallyValidated: true),
            new("Blade 16 - RZ09-0528", "02C6",
                AutomaticInstallationAvailable: true, PhysicallyValidated: false),
            new("Blade 16 - RZ09-0483", "029F",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 14 - RZ09-0482", "029D",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 14 - RZ09-0530", "02C5",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 15 Base Model (Early 2020) - RZ09-0328", "0255",
                AutomaticInstallationAvailable: false, PhysicallyValidated: false),
            new("Blade 18 - RZ09-0484", "02A0",
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
