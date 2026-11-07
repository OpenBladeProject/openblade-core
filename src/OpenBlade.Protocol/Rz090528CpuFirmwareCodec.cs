using System.Buffers.Binary;

namespace OpenBlade.Protocol;

/// <summary>
/// Encodes the observed version-7 AMD_ACPI CPU commands for RZ09-0528.
/// Encoding is not write admission and saved configuration is not live readback.
/// </summary>
internal static class Rz090528CpuFirmwareCodec
{
    public const uint PowerLimitFunction = 0x00050001;
    public const uint CurveOptimizerFunction = 0x0005000A;
    public const int SavedConfigurationLength = 800;
    public const int ObservedCoreCount = 12;
    public const int SavedCoreSlotCount = 16;

    public static byte[] EncodePowerLimit(int watts)
    {
        if (watts is < 45 or > 95)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        return Encode(PowerLimitFunction, checked((uint)watts * 1000));
    }

    public static byte[] EncodeCurveOptimizer(bool enabled, int rememberedValue)
    {
        if (rememberedValue is < -30 or > -1)
        {
            throw new ArgumentOutOfRangeException(nameof(rememberedValue));
        }

        // Mode 1, negative sign, all cores. Disabled retains this valid mode
        // with magnitude zero; firmware mode zero is not a disable command.
        uint magnitude = enabled ? checked((uint)-rememberedValue) : 0;
        return Encode(CurveOptimizerFunction, 0x11FF0000 | magnitude);
    }

    public static Rz090528SavedCpuConfiguration DecodeSaved(ReadOnlySpan<byte> configuration)
    {
        if (configuration.Length != SavedConfigurationLength)
        {
            throw new ArgumentException("The saved firmware layout is not the observed 800-byte layout.", nameof(configuration));
        }

        return new Rz090528SavedCpuConfiguration(configuration.ToArray());
    }

    private static byte[] Encode(uint function, uint argument)
    {
        byte[] command = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(command, function);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), argument);
        return command;
    }
}

/// <summary>
/// Lossless private restoration baseline. Never include its bytes in public
/// diagnostics or treat its values as measurements of the running CPU.
/// </summary>
internal sealed class Rz090528SavedCpuConfiguration
{
    private readonly byte[] configuration;

    internal Rz090528SavedCpuConfiguration(byte[] configuration)
    {
        this.configuration = configuration;
    }

    public byte PolicyMode => configuration[0x94];
    public byte PowerLimitMode => configuration[0x95];
    public uint PowerLimitMilliwatts => ReadUInt32(0x96);
    // Retain every coupled field without assigning unverified units or names
    // to the four sibling fields. Their boot handlers consume all five.
    public IReadOnlyList<uint> PowerLimitFields => Array.AsReadOnly(new uint[]
    {
        ReadUInt32(0x96), ReadUInt32(0x9A), ReadUInt32(0x9E),
        ReadUInt32(0xA2), ReadUInt32(0xA6),
    });
    public byte CurveMode => configuration[0xC0];
    public bool HasCustomPowerLimit => PolicyMode == 2 && PowerLimitMode == 2;

    public int? AllCoreCurveOptimizer => CurveMode == 1
        ? DecodeSigned(configuration[0xC1], ReadUInt16(0xC2))
        : null;

    public int? GetCoreCurveOptimizer(int core)
    {
        if (core is < 0 or >= Rz090528CpuFirmwareCodec.SavedCoreSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(core));
        }

        return CurveMode switch
        {
            1 => AllCoreCurveOptimizer,
            2 => DecodeSigned(configuration[0xC4 + core], ReadUInt16(0x104 + core * 2)),
            _ => null,
        };
    }

    public byte[] CopyBytes() => (byte[])configuration.Clone();

    private uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(configuration.AsSpan(offset, 4));
    private ushort ReadUInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(configuration.AsSpan(offset, 2));
    private static int? DecodeSigned(byte sign, ushort magnitude) => sign switch
    {
        0 => magnitude,
        1 => -magnitude,
        _ => null,
    };
}
