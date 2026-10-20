using System.Buffers.Binary;

namespace OpenBlade.SessionAgent;

internal enum RazerSnapTapInputDecodeResult
{
    Decoded,
    Ignored,
    Malformed,
}

internal sealed class RazerSnapTapHookSnapshot
{
    private readonly byte[] record;

    internal RazerSnapTapHookSnapshot(ReadOnlySpan<byte> record)
    {
        this.record = record.ToArray();
        HookType = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        ScanCode = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
    }

    internal uint HookType { get; }
    internal ushort ScanCode { get; }

    internal byte[] CopyRecord() => (byte[])record.Clone();

    internal bool RecordEquals(ReadOnlySpan<byte> candidate) => candidate.SequenceEqual(record);
}

/// <summary>Fixed A/D codecs for the source-reviewed RzCommon API contract.</summary>
internal static class RazerSnapTapDriverProtocol
{
    internal const uint NotificationIoControlCode = 0x88883018;
    internal const uint SendInputIoControlCode = 0x88883020;
    internal const uint SetInputHookIoControlCode = 0x88883024;
    internal const uint ClearInputHookIoControlCode = 0x8888302C;
    internal const uint EnumInputHookIoControlCode = 0x88883030;
    internal const uint EnableInputHookIoControlCode = 0x88883034;

    internal const int HookIndexLength = 0x04;
    internal const int OutputLength = 0x20;
    internal const int ClearHookLength = 0x20;
    internal const int HookRecordLength = 0x124;
    internal const int NotificationLength = 0x130;

    // The driver returns STATUS_UNSUCCESSFUL when EnumInputHook reaches the end.
    internal const uint EnumTerminalNtStatus = 0xC0000001;
    internal const int EnumTerminalWin32ErrorCode = 31;

    // A client-side corruption bound. The vendor list has no encoded count or static maximum.
    internal const int MaximumHookEnumerationCount = 256;

    private const uint KeyboardHookType = 1;
    private const uint HookNotificationKind = 2;
    private const ushort AScanCode = 0x1E;
    private const ushort DScanCode = 0x20;

    internal static byte[] EncodeHook(ushort scanCode)
    {
        ValidateAdScanCode(scanCode);
        byte[] record = new byte[HookRecordLength];
        WriteKeyboardRecord(record, scanCode, isDown: true);
        return record;
    }

    internal static byte[] EncodeClearHook(ushort scanCode)
    {
        ValidateAdScanCode(scanCode);
        byte[] record = new byte[ClearHookLength];
        WriteKeyboardRecord(record, scanCode, isDown: true);
        return record;
    }

    internal static byte[] EncodeOutput(SnapTapKeyStroke stroke)
    {
        ValidateAdScanCode(stroke.ScanCode);
        byte[] record = new byte[OutputLength];
        WriteKeyboardRecord(record, stroke.ScanCode, stroke.IsDown);
        return record;
    }

    internal static byte[] EncodeHookIndex(uint index)
    {
        byte[] input = new byte[HookIndexLength];
        BinaryPrimitives.WriteUInt32LittleEndian(input, index);
        return input;
    }

    internal static byte[] EncodeHookEnabled(bool enabled)
    {
        byte[] input = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(input, enabled ? 1u : 0u);
        return input;
    }

    internal static bool TryDecodeHook(
        ReadOnlySpan<byte> buffer,
        int bytesReturned,
        out RazerSnapTapHookSnapshot? hook)
    {
        if (bytesReturned != HookRecordLength || buffer.Length < HookRecordLength)
        {
            hook = null;
            return false;
        }

        hook = new RazerSnapTapHookSnapshot(buffer[..HookRecordLength]);
        return true;
    }

    internal static bool IsAdHook(RazerSnapTapHookSnapshot hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return hook.HookType == KeyboardHookType && IsAdScanCode(hook.ScanCode);
    }

    internal static bool IsOwnedAdHook(RazerSnapTapHookSnapshot hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return IsAdHook(hook) && hook.RecordEquals(EncodeHook(hook.ScanCode));
    }

    internal static RazerSnapTapInputDecodeResult DecodeInput(
        ReadOnlySpan<byte> buffer,
        int bytesTransferred,
        out SnapTapKeyStroke stroke)
    {
        stroke = default;
        if (bytesTransferred != NotificationLength || buffer.Length < NotificationLength)
        {
            return RazerSnapTapInputDecodeResult.Malformed;
        }

        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        if (kind != HookNotificationKind)
        {
            return RazerSnapTapInputDecodeResult.Ignored;
        }

        uint hookType = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);
        ushort scanCode = BinaryPrimitives.ReadUInt16LittleEndian(buffer[22..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[24..]);
        if (hookType != KeyboardHookType || !IsAdScanCode(scanCode) || flags > 1)
        {
            return RazerSnapTapInputDecodeResult.Malformed;
        }

        stroke = new SnapTapKeyStroke(scanCode, IsDown: flags == 0);
        return RazerSnapTapInputDecodeResult.Decoded;
    }

    private static void WriteKeyboardRecord(Span<byte> record, ushort scanCode, bool isDown)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], KeyboardHookType);
        BinaryPrimitives.WriteUInt16LittleEndian(record[10..], scanCode);
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], isDown ? (ushort)0 : (ushort)1);
    }

    private static bool IsAdScanCode(ushort scanCode) => scanCode is AScanCode or DScanCode;

    private static void ValidateAdScanCode(ushort scanCode)
    {
        if (!IsAdScanCode(scanCode))
        {
            throw new ArgumentOutOfRangeException(nameof(scanCode));
        }
    }
}
