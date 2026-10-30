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

/// <summary>Closed selector-set codecs for the source-reviewed RzCommon API contract.</summary>
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

    internal static IReadOnlyList<ushort> ScanCodes(RazerKeyboardHookPurpose purpose) => purpose switch
    {
        RazerKeyboardHookPurpose.SnapTap => AdScanCodes,
        RazerKeyboardHookPurpose.FunctionRow => FunctionScanCodes,
        RazerKeyboardHookPurpose.Combined => CombinedScanCodes,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private static readonly IReadOnlyList<ushort> AdScanCodes = Array.AsReadOnly<ushort>([0x1E, 0x20]);
    private static readonly IReadOnlyList<ushort> FunctionScanCodes = Array.AsReadOnly<ushort>(
        [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58]);
    private static readonly IReadOnlyList<ushort> CombinedScanCodes = Array.AsReadOnly<ushort>(
        [0x1E, 0x20, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58]);

    internal static byte[] EncodeHook(ushort scanCode, RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap)
    {
        ValidateScanCode(scanCode, purpose);
        byte[] record = new byte[HookRecordLength];
        WriteKeyboardRecord(record, scanCode, isDown: true);
        return record;
    }

    internal static byte[] EncodeClearHook(ushort scanCode, RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap)
    {
        ValidateScanCode(scanCode, purpose);
        byte[] record = new byte[ClearHookLength];
        WriteKeyboardRecord(record, scanCode, isDown: true);
        return record;
    }

    internal static byte[] EncodeOutput(SnapTapKeyStroke stroke, RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap)
    {
        ValidateScanCode(stroke.ScanCode, purpose);
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

    internal static bool IsPurposeHook(RazerSnapTapHookSnapshot hook, RazerKeyboardHookPurpose purpose) =>
        hook.HookType == KeyboardHookType && ScanCodes(purpose).Contains(hook.ScanCode);

    internal static bool IsOwnedHook(RazerSnapTapHookSnapshot hook, RazerKeyboardHookPurpose purpose) =>
        IsPurposeHook(hook, purpose) && hook.RecordEquals(EncodeHook(hook.ScanCode, purpose));

    internal static RazerSnapTapInputDecodeResult DecodeInput(
        ReadOnlySpan<byte> buffer,
        int bytesTransferred,
        out SnapTapKeyStroke stroke,
        RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap)
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
        if (hookType != KeyboardHookType || flags > 1)
        {
            return RazerSnapTapInputDecodeResult.Malformed;
        }
        if (!ScanCodes(purpose).Contains(scanCode))
        {
            // One worker owns the selected union. Another selector indicates
            // an unexpected global hook: never silently discard its input.
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

    private static void ValidateScanCode(ushort scanCode, RazerKeyboardHookPurpose purpose)
    {
        if (!ScanCodes(purpose).Contains(scanCode))
        {
            throw new ArgumentOutOfRangeException(nameof(scanCode));
        }
    }
}
