namespace OpenBlade.Contracts;

/// <summary>
/// Advisory, current-user view of Windows Dynamic Lighting for the exact laptop
/// keyboard. MayControl means the global Windows switch is on and the exact
/// LampArray is present, so Windows can override OpenBlade keyboard lighting.
/// It is not an ownership signal and never blocks, retires or restores
/// OpenBlade lighting. Unknown means no current observation from the active
/// user's session.
/// </summary>
public enum WindowsDynamicLightingAdvisory
{
    Unknown,
    Off,
    MayControl,
}

/// <summary>
/// One exact model whose keyboard MCU image declares a HID LampArray
/// collection (usage page 0x59, usage 0x01). Listing a model admits only the
/// advisory observation; it does not advertise LampArray delegation.
/// </summary>
/// <param name="ModelNumber">Exact catalog model number.</param>
/// <param name="ProductId">Exact USB product ID of the keyboard LampArray.</param>
/// <param name="CoversVaporChamber">True when the same LampArray also drives the Vapor chamber region.</param>
public sealed record WindowsDynamicLightingLampArrayProfile(
    string ModelNumber,
    ushort ProductId,
    bool CoversVaporChamber);

public static class WindowsDynamicLightingLampArrays
{
    // docs/source-reviewed-blade-models.md#windows-dynamic-lighting-advisory
    // cites the descriptor evidence for each entry. A matching USB ID or WinRT
    // enumeration alone does not admit a model.
    private static readonly WindowsDynamicLightingLampArrayProfile[] Profiles =
    [
        new(RazerBladeModelCatalog.Rz090528ModelNumber, 0x02C6, CoversVaporChamber: false),
        new("RZ09-0529", 0x02C7, CoversVaporChamber: true),
        new("RZ09-0509", 0x02B8, CoversVaporChamber: false),
        new("RZ09-0510", 0x02B7, CoversVaporChamber: false),
        new("RZ09-0530", 0x02C5, CoversVaporChamber: false),
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
