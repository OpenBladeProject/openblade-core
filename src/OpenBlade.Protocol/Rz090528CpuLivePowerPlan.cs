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
    private CpuLivePowerValue(uint? milliwatts, uint bits) { Milliwatts = milliwatts; RawBits = bits; }
    internal uint? Milliwatts { get; }
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
    internal static bool TryFromObservedWatts(uint bits, out CpuLivePowerValue value)
    {
        value = default;
        float watts = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(watts) || watts < 0) { return false; }
        // Phoenix CO peers compare the original IEEE words. The nullable mW
        // projection is display-only and never reconstructs the native sample.
        double rounded = Math.Round((double)watts * 1000, MidpointRounding.ToEven);
        value = new(rounded <= uint.MaxValue ? (uint)rounded : null, bits);
        return true;
    }

    private static uint Bits(uint milliwatts) => BitConverter.SingleToUInt32Bits((float)milliwatts * 0.001f);
}

internal sealed record CpuLivePowerPair(CpuLivePowerValue Fast, CpuLivePowerValue Slow, uint StapmRawBits)
{
    internal static CpuLivePowerPair? DecodeCurvePeer(CpuLiveCurveContract contract, uint stapmBits, uint fastBits, uint slowBits)
    {
        float stapm = BitConverter.UInt32BitsToSingle(stapmBits);
        if (!float.IsFinite(stapm)) { return null; }
        if (contract.UsesPhoenixProtocol)
        {
            return stapm >= 0 && CpuLivePowerValue.TryFromObservedWatts(fastBits, out var fast)
                && CpuLivePowerValue.TryFromObservedWatts(slowBits, out var slow) ? new(fast, slow, stapmBits) : null;
        }
        return contract == CpuLiveCurveContract.Rz090528 && CpuLivePowerValue.TryFromRawBits(fastBits, out var legacyFast)
            && CpuLivePowerValue.TryFromRawBits(slowBits, out var legacySlow) ? new(legacyFast, legacySlow, stapmBits) : null;
    }

    internal CpuLivePowerValue Get(CpuLivePowerLimit limit) => limit switch
    { CpuLivePowerLimit.Fast => Fast, CpuLivePowerLimit.Slow => Slow, _ => throw new ArgumentOutOfRangeException(nameof(limit)) };
}

internal sealed class CpuLivePowerSnapshot
{
    internal const uint SupportedVersion = 0x005D000B;
    internal const int PayloadLength = 0xD50, FastOffset = 8, SlowOffset = 16;
    private readonly CpuPmRawSnapshot source;
    private CpuLivePowerSnapshot(CpuPmRawSnapshot source, CpuLivePowerPair pair) { this.source = source; Pair = pair; }
    internal CpuLiveCurveContract Contract => source.Contract;
    internal CpuLivePowerPair Pair { get; }
    internal DateTimeOffset CapturedUtc => source.CapturedUtc;
    internal long MonotonicTimestamp => source.MonotonicTimestamp;
    internal string PayloadSha256 => source.PayloadSha256;
    internal byte[] CopyOriginalPayload() => source.CopyPayload();
    // The original overload is also used by PPT mutation callers. Its scope remains 0528.
    internal static CpuLivePowerSnapshot? TryCreate(CpuPmRawSnapshot? source) =>
        TryCreate(CpuLiveCurveContract.Rz090528, source);

    internal static CpuLivePowerSnapshot? TryCreate(CpuLiveCurveContract contract, CpuPmRawSnapshot? source)
    {
        if (source is null || source.Contract != contract || source.Validity != CpuPmRawSnapshotValidity.ValidCandidateObservation
            || source.CapturedUtc == default || source.MonotonicTimestamp < 0) { return null; }
        byte[] bytes = source.CopyPayload();
        if (!contract.AcceptsGeometry(contract.EnabledCoreMask, source.PmVersion, bytes.Length)) { return null; }
        var pair = CpuLivePowerPair.DecodeCurvePeer(contract, BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(FastOffset)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SlowOffset)));
        return pair is null ? null : new(source, pair);
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
