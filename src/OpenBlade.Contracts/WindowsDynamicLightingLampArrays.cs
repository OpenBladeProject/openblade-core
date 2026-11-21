namespace OpenBlade.Contracts;

/// <summary>
/// Host-observed Windows Dynamic Lighting state for the exact laptop keyboard.
/// Unknown means no current-user observation has been accepted; it never
/// confirms that Windows released the keyboard.
/// </summary>
public enum WindowsDynamicLightingOwnership
{
    Unknown,
    Released,
    Owned,
}

/// <summary>
/// One exact model whose keyboard MCU image declares a HID LampArray
/// collection (usage page 0x59, usage 0x01). Windows Dynamic Lighting can then
/// take the keyboard through LampArrayControl AutonomousMode without any
/// OpenBlade command observing it.
/// </summary>
/// <param name="ModelNumber">Exact catalog model number.</param>
/// <param name="ProductId">Exact USB product ID of the keyboard LampArray.</param>
/// <param name="FirmwareImage">Reviewed MCU image that declares the collection.</param>
/// <param name="DescriptorOffset">Decoded-image offset of the collection.</param>
/// <param name="CoversVaporChamber">True when the same LampArray also drives the Vapor Chamber region.</param>
public sealed record WindowsDynamicLightingLampArrayProfile(
    string ModelNumber,
    ushort ProductId,
    string FirmwareImage,
    int DescriptorOffset,
    bool CoversVaporChamber);

public static class WindowsDynamicLightingLampArrays
{
    // Each entry was re-read from the decoded, hash-pinned MCU image named in
    // evidence/source-reviewed-blades. A matching USB ID or WinRT enumeration
    // alone does not admit a model. SO690 (RZ09-0483) declares no LampArray.
    // RZ09-0581 (SO6120) is not listed: its descriptor has not been reviewed.
    private static readonly WindowsDynamicLightingLampArrayProfile[] Profiles =
    [
        new(RazerBladeModelCatalog.Rz090528ModelNumber, 0x02C6,
            "SO6110 MCU 1.09.00", 0xDD24, CoversVaporChamber: false),
        new("RZ09-0529", 0x02C7, "KI8110 MCU 1.07.00", 0xEE30, CoversVaporChamber: true),
        new("RZ09-0509", 0x02B8, "KI8100 MCU 1.01.00", 0xEDE8, CoversVaporChamber: false),
        new("RZ09-0510", 0x02B7, "SO6100 MCU 1.03.00", 0x1023C, CoversVaporChamber: false),
        new("RZ09-0530", 0x02C5, "PI4110 MCU 1.04.00", 0xC30C, CoversVaporChamber: false),
    ];

    public static IReadOnlyList<WindowsDynamicLightingLampArrayProfile> All { get; } =
        Array.AsReadOnly(Profiles);

    public static WindowsDynamicLightingLampArrayProfile? Find(string? modelNumber)
    {
        string? exact = RazerBladeModelCatalog.Find(modelNumber)?.ModelNumber;
        return exact is null
            ? null
            : Profiles.FirstOrDefault(profile =>
                string.Equals(profile.ModelNumber, exact, StringComparison.Ordinal));
    }
}
