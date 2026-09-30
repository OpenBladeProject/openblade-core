using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// Pure representations and planning only. None of these types grants native
// access, proves freshness, establishes ownership, or executes a command.
internal sealed class CpuLiveCurveSnapshot
{
    internal const uint SupportedMask = 0xFF55;
    internal const uint SupportedPmVersion = 0x005D000B;
    internal const int SupportedPmLength = 0xD50;
    internal const int CoreCount = 12;

    private readonly IReadOnlyList<CpuLiveCoreOffset> cores;
    private CpuLiveCurveSnapshot(CpuLiveCoreOffset[] values) => cores = Array.AsReadOnly(values);
    internal IReadOnlyList<CpuLiveCoreOffset> CoreOffsets => cores;

    internal static CpuLiveCurveSnapshot? TryCreate(Rz090528CpuLiveObservation? observation)
    {
        if (observation is not
            { EnabledCoreMask: SupportedMask, PmVersion: SupportedPmVersion, PmTableLength: SupportedPmLength, CoreOffsets.Count: CoreCount })
        { return null; }
        var copy = new CpuLiveCoreOffset[CoreCount];
        uint seen = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            var core = observation.CoreOffsets[i];
            if (core is null || !IsEnabled(core.PhysicalCoreId)
                || (seen & (1u << core.PhysicalCoreId)) != 0) { return null; }
            seen |= 1u << core.PhysicalCoreId;
            copy[i] = new(core.PhysicalCoreId, core.Offset);
        }
        if (seen != SupportedMask) { return null; }
        Array.Sort(copy, (left, right) => left.PhysicalCoreId.CompareTo(right.PhysicalCoreId));
        return new(copy);
    }

    internal static bool IsEnabled(int physicalCoreId) => physicalCoreId is >= 0 and < 16
        && (SupportedMask & (1u << physicalCoreId)) != 0;

    internal CpuLiveCurveSnapshot WithOffset(CpuLiveCurveCommand command)
    {
        // Commands can only contain an enabled ID and a signed16 value.
        return new(cores.Select(core => new CpuLiveCoreOffset(core.PhysicalCoreId,
            core.PhysicalCoreId == command.PhysicalCoreId ? command.Offset : core.Offset)).ToArray());
    }
}

internal readonly record struct CpuLiveCurveCommand
{
    internal const uint Message = 0x53;
    private CpuLiveCurveCommand(int physicalCoreId, short offset)
    { PhysicalCoreId = physicalCoreId; Offset = offset; }
    internal int PhysicalCoreId { get; }
    internal short Offset { get; }

    // Representation is deliberately broader than forward editor permission.
    // Every planned forward and restore command needs independent admission.
    internal static bool TryCreate(int physicalCoreId, int offset, uint enabledMask,
        out CpuLiveCurveCommand command)
    {
        command = default;
        if (enabledMask != CpuLiveCurveSnapshot.SupportedMask || !CpuLiveCurveSnapshot.IsEnabled(physicalCoreId)
            || offset is < short.MinValue or > short.MaxValue) { return false; }
        command = new(physicalCoreId, (short)offset);
        return true;
    }

    internal uint EncodeArgument() => ((uint)(PhysicalCoreId / 8) << 28)
        | ((uint)(PhysicalCoreId % 8) << 20) | unchecked((ushort)Offset);

    internal static bool TryDecode(uint message, uint argument, uint enabledMask,
        out CpuLiveCurveCommand command)
    {
        command = default;
        if (message != Message || (argument & 0xEF8F0000) != 0) { return false; }
        int physicalId = (int)(((argument >> 28) & 1) * 8 + ((argument >> 20) & 7));
        return TryCreate(physicalId, unchecked((short)(argument & 0xFFFF)), enabledMask, out command)
            && command.EncodeArgument() == argument;
    }
}

internal sealed record CpuLiveCurveTarget(int PhysicalCoreId, int Offset);
internal enum CpuLiveCurveCommandPurpose { Forward, Restore }
internal enum CpuLiveCurvePlanStatus { Prepared, Unchanged, InvalidBaseline, InvalidTarget, AdmissionDenied }
internal sealed record CpuLiveCurveStep(CpuLiveCurveCommand Command, CpuLiveCurveCommand RestoreCommand,
    CpuLiveCurveSnapshot Before, CpuLiveCurveSnapshot After);
internal sealed record CpuLiveCurvePlanResult(CpuLiveCurvePlanStatus Status,
    CpuLiveCurveSnapshot? Baseline, Rz090528CpuLiveCurvePlan? Plan);

internal sealed class Rz090528CpuLiveCurvePlan
{
    private Rz090528CpuLiveCurvePlan(CpuLiveCurveSnapshot baseline, CpuLiveCurveStep[] steps)
    {
        Baseline = baseline;
        Steps = Array.AsReadOnly(steps);
    }

    internal CpuLiveCurveSnapshot Baseline { get; }
    internal IReadOnlyList<CpuLiveCurveStep> Steps { get; }
    internal CpuLiveCurveSnapshot Expected => Steps[^1].After;

    internal static CpuLiveCurvePlanResult CreateAllCore(Rz090528CpuLiveObservation? observation,
        CpuCurveOptimizerSettings? settings,
        Func<CpuLiveCurveCommand, CpuLiveCurveCommandPurpose, bool>? admit)
    {
        var baseline = CpuLiveCurveSnapshot.TryCreate(observation);
        if (baseline is null) { return new(CpuLiveCurvePlanStatus.InvalidBaseline, null, null); }
        if (settings is null) { return new(CpuLiveCurvePlanStatus.Unchanged, baseline, null); }
        if (!settings.IsWellFormed) { return new(CpuLiveCurvePlanStatus.InvalidTarget, baseline, null); }
        return Create(baseline, baseline.CoreOffsets.Select(core =>
            new CpuLiveCurveTarget(core.PhysicalCoreId, settings.EffectiveValue)).ToArray(), admit);
    }

    internal static CpuLiveCurvePlanResult CreateSelective(Rz090528CpuLiveObservation? observation,
        IReadOnlyList<CpuLiveCurveTarget>? targets,
        Func<CpuLiveCurveCommand, CpuLiveCurveCommandPurpose, bool>? admit)
    {
        var baseline = CpuLiveCurveSnapshot.TryCreate(observation);
        if (baseline is null) { return new(CpuLiveCurvePlanStatus.InvalidBaseline, null, null); }
        int count = targets?.Count ?? 0;
        if (count == 0) { return new(CpuLiveCurvePlanStatus.Unchanged, baseline, null); }
        if (count is < 0 or > CpuLiveCurveSnapshot.CoreCount) { return new(CpuLiveCurvePlanStatus.InvalidTarget, baseline, null); }
        // Freeze caller-owned collections before invoking admission callbacks.
        var copy = new CpuLiveCurveTarget[count];
        for (int i = 0; i < copy.Length; i++) { copy[i] = targets![i]; }
        return Create(baseline, copy, admit);
    }

    private static CpuLiveCurvePlanResult Create(CpuLiveCurveSnapshot baseline,
        CpuLiveCurveTarget[] targets, Func<CpuLiveCurveCommand, CpuLiveCurveCommandPurpose, bool>? admit)
    {
        uint seen = 0;
        foreach (var target in targets)
        {
            if (target is null || !CpuLiveCurveSnapshot.IsEnabled(target.PhysicalCoreId)
                || target.Offset is < -30 or > 0 || (seen & (1u << target.PhysicalCoreId)) != 0)
            { return new(CpuLiveCurvePlanStatus.InvalidTarget, baseline, null); }
            seen |= 1u << target.PhysicalCoreId;
        }

        var steps = new List<CpuLiveCurveStep>(CpuLiveCurveSnapshot.CoreCount);
        var current = baseline;
        foreach (var target in targets.OrderBy(target => target.PhysicalCoreId))
        {
            short previous = current.CoreOffsets.Single(core => core.PhysicalCoreId == target.PhysicalCoreId).Offset;
            if (previous == target.Offset) { continue; }
            _ = CpuLiveCurveCommand.TryCreate(target.PhysicalCoreId, target.Offset, CpuLiveCurveSnapshot.SupportedMask, out var forward);
            _ = CpuLiveCurveCommand.TryCreate(target.PhysicalCoreId, previous, CpuLiveCurveSnapshot.SupportedMask, out var reverse);
            var after = current.WithOffset(forward);
            steps.Add(new(forward, reverse, current, after));
            current = after;
        }
        if (steps.Count == 0) { return new(CpuLiveCurvePlanStatus.Unchanged, baseline, null); }
        if (admit is null) { return new(CpuLiveCurvePlanStatus.AdmissionDenied, baseline, null); }
        try
        {
            foreach (var step in steps)
            {
                if (!admit(step.Command, CpuLiveCurveCommandPurpose.Forward)
                    || !admit(step.RestoreCommand, CpuLiveCurveCommandPurpose.Restore))
                { return new(CpuLiveCurvePlanStatus.AdmissionDenied, baseline, null); }
            }
        }
        catch (Exception)
        {
            // Planning exposes no partially admitted plan or exception details.
            return new(CpuLiveCurvePlanStatus.AdmissionDenied, baseline, null);
        }
        var plan = new Rz090528CpuLiveCurvePlan(baseline, steps.ToArray());
        return new(CpuLiveCurvePlanStatus.Prepared, baseline, plan);
    }
}
