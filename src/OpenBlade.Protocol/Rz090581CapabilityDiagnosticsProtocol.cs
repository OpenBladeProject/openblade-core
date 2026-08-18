using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class Rz090581CapabilityDiagnosticsProtocol
{
    internal const byte CommandClass = 0x0D;
    internal const byte ThermalFanIdListCommandId = 0x80;
    internal const byte MiniLedPanelResolutionCommandId = 0x8E;
    internal const byte SkuHardwareConfigurationCommandId = 0x8F;
    internal const int SingleBytePayloadLength = 1;
    internal const int ThermalFanIdListPayloadLength = 80;

    public static bool TryDecodeRawByte(ReadOnlySpan<byte> payload, out byte value)
    {
        if (payload.Length != SingleBytePayloadLength)
        {
            value = 0;
            return false;
        }

        value = payload[0];
        return true;
    }

    public static bool TryDecodeThermalFanIds(
        ReadOnlySpan<byte> payload,
        out IReadOnlyList<byte> fanIds)
    {
        if (payload.Length != ThermalFanIdListPayloadLength
            || payload[0] > ThermalFanIdListPayloadLength - 1)
        {
            fanIds = [];
            return false;
        }

        fanIds = payload.Slice(1, payload[0]).ToArray();
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> skuHardwareConfiguration,
        ReadOnlySpan<byte> miniLedPanelResolution,
        ReadOnlySpan<byte> thermalFanIdList,
        out Rz090581CapabilityDiagnostics? diagnostics)
    {
        if (!TryDecodeRawByte(skuHardwareConfiguration, out byte skuRaw)
            || !TryDecodeRawByte(miniLedPanelResolution, out byte miniLedRaw)
            || !TryDecodeThermalFanIds(thermalFanIdList, out IReadOnlyList<byte> fanIds))
        {
            diagnostics = null;
            return false;
        }

        diagnostics = new Rz090581CapabilityDiagnostics(skuRaw, miniLedRaw, fanIds);
        return true;
    }
}
