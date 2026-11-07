using System.Buffers.Binary;

namespace OpenBlade.Protocol;

// Pure source-mapped observations and planning. These values cannot establish
// freshness, native permission, ownership, a firmware clamp rule or restoration.
internal enum CpuLivePowerLimit { Fast, Slow }

internal readonly record struct CpuLivePowerValue
{
    // The vendor cap is a ten-bit whole-watt field. This is a representation
    // bound, not an assertion that this machine admits the maximum.
    internal const uint MaximumMilliwatts = 1023_000;
    private CpuLivePowerValue(uint milliwatts, uint bits) { Milliwatts = milliwatts; RawBits = bits; }
    internal uint Milliwatts { get; }
    internal uint RawBits { get; }
    internal float Watts => BitConverter.UInt32BitsToSingle(RawBits);
    internal static bool TryFromMilliwatts(uint milliwatts, out CpuLivePowerValue value)
    {
        value = default;
        if (milliwatts is 0 or > MaximumMilliwatts) { return false; }
        value = new(milliwatts, Bits(milliwatts)); return true;
    }
    internal static bool TryFromRawBits(uint bits, out CpuLivePowerValue value)
    {
        value = default;
        float watts = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(watts) || watts <= 0) { return false; }
        // Positive finite binary32 bit ordering is monotonic. Find the first
        // candidate, then prove exact reconstruction and uniqueness within the
        // bounded supported integer-mW domain. Never round with a tolerance.
        uint low = 1, high = MaximumMilliwatts;
        while (low < high)
        {
            uint middle = low + (high - low) / 2;
            if (Bits(middle) < bits) { low = middle + 1; } else { high = middle; }
        }
        if (Bits(low) != bits || low > 1 && Bits(low - 1) == bits
            || low < MaximumMilliwatts && Bits(low + 1) == bits) { return false; }
        value = new(low, bits); return true;
    }
    private static uint Bits(uint milliwatts) => BitConverter.SingleToUInt32Bits((float)milliwatts * 0.001f);
}

internal sealed record CpuLivePowerPair(CpuLivePowerValue Fast, CpuLivePowerValue Slow, uint StapmRawBits)
{
    internal CpuLivePowerValue Get(CpuLivePowerLimit limit) => limit switch
    { CpuLivePowerLimit.Fast => Fast, CpuLivePowerLimit.Slow => Slow, _ => throw new ArgumentOutOfRangeException(nameof(limit)) };
}

internal sealed class CpuLivePowerSnapshot
{
    internal const uint SupportedVersion = 0x005D000B;
    internal const int PayloadLength = 0xD50, FastOffset = 8, SlowOffset = 16;
    private readonly CpuPmRawSnapshot source;
    private CpuLivePowerSnapshot(CpuPmRawSnapshot source, CpuLivePowerPair pair) { this.source = source; Pair = pair; }
    internal CpuLivePowerPair Pair { get; }
    internal DateTimeOffset CapturedUtc => source.CapturedUtc;
    internal long MonotonicTimestamp => source.MonotonicTimestamp;
    internal string PayloadSha256 => source.PayloadSha256;
    internal byte[] CopyOriginalPayload() => source.CopyPayload();
    internal static CpuLivePowerSnapshot? TryCreate(CpuPmRawSnapshot? source)
    {
        // CpuPmRawSnapshot's private factory already enforces the exact B
        // version, D54 envelope and D50 payload. Revalidate through that factory
        // so this layer cannot substitute another version/layout.
        if (source is null || source.Validity != CpuPmRawSnapshotValidity.ValidCandidateObservation
            || source.CapturedUtc == default || source.MonotonicTimestamp < 0
            || !CpuPmRawSnapshot.TryCreate(SupportedVersion, new(true, source.CopyReply()),
                source.CapturedUtc, source.MonotonicTimestamp, out var checkedSource)) { return null; }
        byte[] bytes = checkedSource!.CopyPayload();
        if (bytes.Length != PayloadLength
            || !CpuLivePowerValue.TryFromRawBits(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(FastOffset, 4)), out var fast)
            || !CpuLivePowerValue.TryFromRawBits(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SlowOffset, 4)), out var slow)) { return null; }
        return new(checkedSource, new(fast, slow, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4))));
    }
}

// Decodes vendor _GetPBOLimits object+7BC, NOT raw PM payload+7BC.
// Supplying these raw register values is not evidence that a read occurred.
internal sealed class CpuLivePowerFusedCap
{
    internal const uint RegisterA0F = 0x03820A0F, RegisterA0B = 0x03820A0B;
    private CpuLivePowerFusedCap(uint a0f, uint a0b, uint watts) { RawA0F = a0f; RawA0B = a0b; Watts = watts; }
    internal uint RawA0F { get; }
    internal uint RawA0B { get; }
    internal uint Watts { get; }
    internal uint Milliwatts => Watts * 1000;
    internal static CpuLivePowerFusedCap? TryDecode(uint rawA0F, uint rawA0B)
    {
        if (rawA0F == uint.MaxValue || rawA0B == uint.MaxValue) { return null; }
        uint watts = ((rawA0F & 0x3F) << 4) | (rawA0B >> 28);
        return watts is >= 1 and <= 1023 ? new(rawA0F, rawA0B, watts) : null;
    }
}

// Decoded candidate data only. This never attests a fresh 82 invocation or
// authorizes mutation. Exact vendor AA160 applies its fused ceiling only when
// capability82 bit1 (DAT_1801161fb) is clear; every other admission still applies.
internal sealed class CpuLivePowerCapability
{
    private CpuLivePowerCapability(uint rawValue) => RawValue = rawValue;
    internal uint RawValue { get; }
    internal bool AppliesFusedCeiling => (RawValue & 2) == 0;
    internal static CpuLivePowerCapability? TryDecode(uint rawValue) => rawValue == uint.MaxValue ? null : new(rawValue);
}

// Decoded and admitted observation metadata only; it supplies no setter authority.
internal sealed record CpuLivePowerTrustedRange(uint Selector, uint MinimumMilliwatts,
    uint MaximumMilliwatts, uint StepMilliwatts);
