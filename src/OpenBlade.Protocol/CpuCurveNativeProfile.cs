using System.Numerics;

namespace OpenBlade.Protocol;

// A closed codec selection, never native authority. Callers still need fresh
// exact-device, installed-backend and operation admission at every boundary.
internal sealed class CpuCurveNativeProfile
{
    private CpuCurveNativeProfile(bool phoenix) => IsPhoenix = phoenix;
    private static readonly CpuCurveNativeProfile Strix = new(false), Phoenix = new(true);

    internal static CpuCurveNativeProfile ForContract(CpuLiveCurveContract contract) =>
        (contract.Id, contract.Version) switch
        {
            ("rz09-0528-v31", 1) => Strix,
            ("rz09-0508-v22", 1) => Phoenix,
            ("rz09-0482-v22", 1) => Phoenix,
            _ => throw new ArgumentException("Unreviewed CPU native contract.", nameof(contract)),
        };

    internal bool IsPhoenix { get; }
    internal uint RootId => IsPhoenix ? 0x14E81022u : CpuQueryPciTopology.RootId;
    internal uint NodeId => IsPhoenix ? 0x14F01022u : CpuQueryPciTopology.NodeId;
    internal uint OrientationId => IsPhoenix ? 0x14F11022u : CpuQueryPciTopology.OrientationId;
    internal uint FuseAddress => IsPhoenix ? 0x005D528u : 0x03820AB0u;
    internal int FuseShift => IsPhoenix ? 7 : 5;
    internal uint MaskBits => IsPhoenix ? 0xFFu : 0xFFFFu;
    internal int CoreCount => IsPhoenix ? 8 : 12;
    internal uint GetterMessage => IsPhoenix ? 0xE1u : 0x5Eu;
    internal uint SetterMessage => 0x53;
    // V22 iterates exactly eight physical-core bits; upper register bits are unrelated.
    internal uint DecodeOrientation(uint value) => IsPhoenix ? value & 0xFF : value;
    internal uint DecodeFuse(uint value) => ~(value >> FuseShift) & MaskBits;
    internal bool AcceptsObservedMask(uint value) => (value & ~MaskBits) == 0
        && BitOperations.PopCount(value) == CoreCount;
    internal uint GetterArgument(int physicalId) => IsPhoenix ? checked((uint)physicalId)
        : ((uint)(physicalId / 8) << 28) | ((uint)(physicalId % 8) << 20);
    internal uint SetterArgument(CpuLiveCurveCommand command)
    {
        if (command.Contract is null || ForContract(command.Contract) != this
            || !command.Contract.IsEnabled(command.PhysicalCoreId))
        { throw new ArgumentException("CPU command belongs to another native contract.", nameof(command)); }
        return IsPhoenix ? ((uint)command.PhysicalCoreId << 20) | unchecked((ushort)command.Offset)
            : command.EncodeArgument();
    }

    internal int PmLength(uint version) => IsPhoenix ? version switch
    {
        0x004C0008 => 0xAF0,
        _ => 0,
    } : version switch
    {
        0x005D0008 => 0xD54,
        0x005D0009 => 0xD58,
        0x005D000A => 0xD60,
        0x005D000B => 0xD50,
        0x005D000C => 0xD68,
        _ => 0,
    };
}
