namespace OpenSynapse.Protocol;

public static class TargetBladeDevice
{
    public const ushort VendorId = 0x1532;
    public const ushort ProductId = 0x02E0;

    public static bool Matches(ushort vendorId, ushort productId) =>
        vendorId == VendorId && productId == ProductId;
}
