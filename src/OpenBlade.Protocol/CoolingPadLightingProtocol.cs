using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Offline codecs for captured cooling-pad lighting command semantics. This type
/// deliberately has no HID handle, transport, retry, or production-admission surface.
/// </summary>
internal static class CoolingPadLightingProtocol
{
    internal const byte OperationClass = 0x0F;
    internal const byte FrameOperationId = 0x03;
    internal const byte BrightnessSetOperationId = 0x04;
    internal const byte BrightnessQueryOperationId = 0x84;
    internal const int FramePayloadLength = 59;

    private const byte FrameProfileId = 0x00;
    private const byte FrameRegionId = 0x00;
    private const byte FrameRow = 0x00;
    private const byte FrameStart = 0x00;
    private const byte FrameEnd = 0x11;
    private const byte BrightnessProfileId = 0x01;
    private const byte BrightnessQueryRegionId = 0x05;
    private const byte BrightnessSetRegionId = 0x00;
    private const int FrameRgbOffset = 5;

    internal static RazerReport CreateFrame(byte transactionId, CoolingPadLightingFrame frame)
    {
        RazerCommandSpec command = CreateFrameSetOnlyCommand(frame);
        return RazerReport.Create(
            transactionId,
            command.ClassId,
            command.CommandId,
            command.Arguments.Span);
    }

    internal static RazerCommandSpec CreateFrameSetOnlyCommand(
        CoolingPadLightingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        byte[] payload = new byte[FramePayloadLength];
        payload[0] = FrameProfileId;
        payload[1] = FrameRegionId;
        payload[2] = FrameRow;
        payload[3] = FrameStart;
        payload[4] = FrameEnd;

        for (int slot = 0; slot < CoolingPadLightingFrame.SlotCount; slot++)
        {
            RgbColor color = frame[slot];
            int offset = FrameRgbOffset + (slot * 3);
            payload[offset] = color.Red;
            payload[offset + 1] = color.Green;
            payload[offset + 2] = color.Blue;
        }

        return new RazerCommandSpec(
            OperationClass,
            FrameOperationId,
            payload,
            FramePayloadLength,
            response => response.Span.SequenceEqual(payload));
    }

    internal static RazerReport CreateBrightnessQuery(byte transactionId) =>
        RazerReport.Create(
            transactionId,
            OperationClass,
            BrightnessQueryOperationId,
            [BrightnessProfileId, BrightnessQueryRegionId, 0x00]);

    internal static RazerReport CreateBrightnessSet(byte transactionId, byte percent) =>
        RazerReport.Create(
            transactionId,
            OperationClass,
            BrightnessSetOperationId,
            [BrightnessProfileId, BrightnessSetRegionId, PercentToRaw(percent)]);

    internal static RazerCommandSpec CreateBrightnessQueryCommand() => new(
        OperationClass,
        BrightnessQueryOperationId,
        [BrightnessProfileId, BrightnessQueryRegionId, 0x00],
        3,
        response => TryDecodeBrightnessQuery(response.Span, out _));

    internal static RazerCommandSpec CreateBrightnessSetCommand(byte percent) =>
        CreateBrightnessRawSetCommand(PercentToRaw(percent));

    internal static RazerCommandSpec CreateBrightnessRawSetCommand(byte raw) => new(
        OperationClass,
        BrightnessSetOperationId,
        [BrightnessProfileId, BrightnessSetRegionId, raw],
        3,
        response => TryDecodeBrightnessRawSetAck(response.Span, raw));

    internal static bool TryDecodeBrightnessQuery(
        ReadOnlySpan<byte> payload,
        out CoolingPadBrightnessReading reading)
    {
        reading = default;
        if (payload.Length != 3
            || payload[0] != BrightnessProfileId
            || payload[1] != BrightnessQueryRegionId)
        {
            return false;
        }

        reading = new CoolingPadBrightnessReading(payload[2]);
        return true;
    }

    internal static bool TryDecodeBrightnessSetAck(
        ReadOnlySpan<byte> payload,
        byte expectedPercent) =>
        TryDecodeBrightnessRawSetAck(payload, PercentToRaw(expectedPercent));

    internal static bool TryDecodeBrightnessRawSetAck(
        ReadOnlySpan<byte> payload,
        byte expectedRaw) =>
        payload.Length == 3
        && payload[0] == BrightnessProfileId
        && payload[1] == BrightnessSetRegionId
        && payload[2] == expectedRaw;

    internal static byte PercentToRaw(byte percent)
    {
        if (percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent));
        }

        return checked((byte)((percent * byte.MaxValue) / 100));
    }

    internal static byte RawToPercent(byte raw) =>
        checked((byte)(((raw * 100) + byte.MaxValue - 1) / byte.MaxValue));
}

internal readonly record struct CoolingPadBrightnessReading(byte Raw)
{
    internal byte Percent => CoolingPadLightingProtocol.RawToPercent(Raw);
}
