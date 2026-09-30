using System.Buffers.Binary;

namespace OpenBlade.Protocol;

// Pure source-mapped observations and planning. These values cannot establish
// freshness, native permission, ownership, a firmware clamp rule or restoration.
internal enum CpuLivePowerLimit { Fast, Slow }
internal enum CpuLivePowerCommandPurpose { Forward, Restore }

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
    internal CpuLivePowerPair With(CpuLivePowerCommand command)
    {
        if (!CpuLivePowerValue.TryFromMilliwatts(command.Milliwatts, out var value)) { throw new ArgumentException("Invalid power command."); }
        return command.Limit switch
        {
            CpuLivePowerLimit.Fast => this with { Fast = value },
            CpuLivePowerLimit.Slow => this with { Slow = value },
            _ => throw new ArgumentException("Invalid power limit."),
        };
    }
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

internal readonly record struct CpuLivePowerCommand
{
    private CpuLivePowerCommand(CpuLivePowerLimit limit, uint milliwatts) { Limit = limit; Milliwatts = milliwatts; }
    internal CpuLivePowerLimit Limit { get; }
    internal uint Milliwatts { get; }
    internal uint Message => Limit switch
    { CpuLivePowerLimit.Fast => 0x32, CpuLivePowerLimit.Slow => 0x33, _ => 0 };
    internal static bool TryCreate(CpuLivePowerLimit limit, uint milliwatts, out CpuLivePowerCommand command)
    {
        command = default;
        if (limit is not (CpuLivePowerLimit.Fast or CpuLivePowerLimit.Slow)
            || !CpuLivePowerValue.TryFromMilliwatts(milliwatts, out _)) { return false; }
        command = new(limit, milliwatts); return true;
    }
    internal uint EncodeArgument() => Milliwatts is > 0 and <= CpuLivePowerValue.MaximumMilliwatts
        && Message is 0x32 or 0x33 ? Milliwatts : throw new InvalidOperationException("Invalid power command.");
    internal static bool TryDecode(uint message, uint argument, out CpuLivePowerCommand command)
    {
        command = default;
        return message is 0x32 or 0x33 && TryCreate(message == 0x32 ? CpuLivePowerLimit.Fast : CpuLivePowerLimit.Slow, argument, out command);
    }
}

internal sealed record CpuLivePowerTarget(CpuLivePowerLimit Limit, int WholeWatts);
internal enum CpuLivePowerPlanStatus { Prepared, Unchanged, InvalidBaseline, InvalidTarget, InvalidCap, AdmissionDenied }
internal sealed record CpuLivePowerStep(CpuLivePowerCommand Command, CpuLivePowerCommand RestoreCommand,
    CpuLivePowerPair Before, CpuLivePowerPair After);
internal sealed record CpuLivePowerPlanResult(CpuLivePowerPlanStatus Status, CpuLivePowerSnapshot? Baseline, Rz090528CpuLivePowerPlan? Plan);

internal sealed class Rz090528CpuLivePowerPlan
{
    private Rz090528CpuLivePowerPlan(CpuLivePowerSnapshot baseline, CpuLivePowerFusedCap cap, CpuLivePowerStep[] steps)
    { Baseline = baseline; FusedCap = cap; Steps = Array.AsReadOnly(steps); }
    internal CpuLivePowerSnapshot Baseline { get; }
    internal CpuLivePowerFusedCap FusedCap { get; }
    internal IReadOnlyList<CpuLivePowerStep> Steps { get; }
    internal CpuLivePowerPair Expected => Steps[^1].After;
    internal static CpuLivePowerPlanResult CreateSelective(CpuPmRawSnapshot? observation,
        IReadOnlyList<CpuLivePowerTarget>? targets, CpuLivePowerFusedCap? cap,
        Func<CpuLivePowerCommand, CpuLivePowerCommandPurpose, bool>? admit)
    {
        var baseline = CpuLivePowerSnapshot.TryCreate(observation);
        if (baseline is null) { return new(CpuLivePowerPlanStatus.InvalidBaseline, null, null); }
        int count = targets?.Count ?? 0;
        if (count == 0) { return new(CpuLivePowerPlanStatus.Unchanged, baseline, null); }
        if (count is < 0 or > 2) { return new(CpuLivePowerPlanStatus.InvalidTarget, baseline, null); }
        var copy = new CpuLivePowerTarget[count]; uint seen = 0;
        for (int i = 0; i < count; i++)
        {
            var target = targets![i];
            if (target is null || target.Limit is not (CpuLivePowerLimit.Fast or CpuLivePowerLimit.Slow)
                || target.WholeWatts is < 45 or > 95 || (seen & (1u << (int)target.Limit)) != 0)
            { return new(CpuLivePowerPlanStatus.InvalidTarget, baseline, null); }
            seen |= 1u << (int)target.Limit; copy[i] = target;
        }
        var steps = new List<CpuLivePowerStep>(2); var current = baseline.Pair;
        foreach (var target in copy.OrderBy(value => value.Limit))
        {
            var previous = current.Get(target.Limit); uint next = (uint)target.WholeWatts * 1000;
            if (previous.Milliwatts == next) { continue; }
            _ = CpuLivePowerCommand.TryCreate(target.Limit, next, out var forward);
            _ = CpuLivePowerCommand.TryCreate(target.Limit, previous.Milliwatts, out var restore);
            var after = current.With(forward); steps.Add(new(forward, restore, current, after)); current = after;
        }
        if (steps.Count == 0) { return new(CpuLivePowerPlanStatus.Unchanged, baseline, null); }
        if (cap is null || steps.Any(step => step.Command.Milliwatts > cap.Milliwatts))
        { return new(CpuLivePowerPlanStatus.InvalidCap, baseline, null); }
        if (admit is null) { return new(CpuLivePowerPlanStatus.AdmissionDenied, baseline, null); }
        try
        {
            // The restore callback deliberately sees the captured mW value even
            // outside the forward editor range. It must apply independent exact
            // restore/cap/context authority before any plan is returned.
            foreach (var step in steps)
            {
                if (!admit(step.Command, CpuLivePowerCommandPurpose.Forward)
                    || !admit(step.RestoreCommand, CpuLivePowerCommandPurpose.Restore))
                { return new(CpuLivePowerPlanStatus.AdmissionDenied, baseline, null); }
            }
        }
        catch (Exception) { return new(CpuLivePowerPlanStatus.AdmissionDenied, baseline, null); }
        return new(CpuLivePowerPlanStatus.Prepared, baseline, new(baseline, cap, steps.ToArray()));
    }
}
