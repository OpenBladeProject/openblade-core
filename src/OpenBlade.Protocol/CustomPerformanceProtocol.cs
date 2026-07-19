using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal enum CustomPerformanceDomain : byte
{
    Cpu = 0x01,
    Gpu = 0x02,
}

internal static class CustomPerformanceProtocol
{
    internal const byte CommandClass = 0x0D;
    internal const byte SetCommandId = 0x07;
    internal const byte QueryCommandId = 0x87;
    private const byte PerformanceClass = 0x01;

    internal static byte[] CreateQueryPayload(CustomPerformanceDomain domain)
    {
        ValidateDomain(domain);
        return [PerformanceClass, (byte)domain, 0x00];
    }

    internal static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        CustomPerformanceDomain expectedDomain,
        out CustomPerformanceLevel level)
    {
        level = default;
        if (!Enum.IsDefined(expectedDomain)
            || payload.Length != 3
            || payload[0] != PerformanceClass
            || payload[1] != (byte)expectedDomain)
        {
            return false;
        }

        return payload[2] switch
        {
            0x00 => Assign(CustomPerformanceLevel.Low, out level),
            0x01 => Assign(CustomPerformanceLevel.Medium, out level),
            0x02 => Assign(CustomPerformanceLevel.High, out level),
            _ => false,
        };
    }

    private static bool Assign(
        CustomPerformanceLevel value,
        out CustomPerformanceLevel level)
    {
        level = value;
        return true;
    }

    private static void ValidateDomain(CustomPerformanceDomain domain)
    {
        if (!Enum.IsDefined(domain))
        {
            throw new ArgumentOutOfRangeException(nameof(domain));
        }
    }
}
