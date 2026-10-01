namespace OpenBlade.Protocol;

// CPU correlation only. BIOS is provenance, not hardware or continuity authority.
// Global DeviceIdentity equality remains unchanged for other consumers.
internal static class CpuHardwareIdentity
{
    internal static bool IsRz090528(DeviceIdentity? identity) => identity is
    { Model: "RZ09-0528", VendorId: 0x1532, ProductId: 0x02C6 };

    internal static bool Same(DeviceIdentity? left, DeviceIdentity? right) =>
        left is not null && right is not null && (left with { BiosVersion = right.BiosVersion }) == right;
}
