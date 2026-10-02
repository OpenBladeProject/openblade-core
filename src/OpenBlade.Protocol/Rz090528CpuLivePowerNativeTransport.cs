using System.Diagnostics;

namespace OpenBlade.Protocol;

// Correlation only; this does not establish ownership or continuity.
internal sealed class CpuLivePowerWriteScope;

internal sealed class CpuLivePowerNativeRequest
{
    internal CpuLivePowerNativeRequest(CpuLivePowerWriteScope scope, CpuLivePowerCommand command,
        CpuLivePowerCommandPurpose purpose, CpuLivePowerPair before, CpuLivePowerPair original, CpuLiveCurvePeerState? curvePeer = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ValidPair(before) || !ValidPair(original) || before.StapmRawBits != original.StapmRawBits || !Enum.IsDefined(purpose)
            || !CpuLivePowerCommand.TryDecode(command.Message, command.EncodeArgument(), out var decoded) || decoded != command
            || purpose == CpuLivePowerCommandPurpose.Forward && (command.Milliwatts is < 45000 or > 95000 || command.Milliwatts % 1000 != 0))
        { throw new ArgumentException("Invalid exact power operation."); }
        Scope = scope; Command = command; Purpose = purpose; Before = before; Original = original; CurvePeer = curvePeer;
        After = before.With(command);
        _ = CpuLivePowerCommand.TryCreate(command.Limit, before.Get(command.Limit).Milliwatts, out var reverse);
        ImmediateReverse = reverse;
        var restores = new List<CpuLivePowerCommand>(2);
        foreach (var limit in new[] { CpuLivePowerLimit.Fast, CpuLivePowerLimit.Slow })
        {
            if (After.Get(limit) == original.Get(limit)) { continue; }
            _ = CpuLivePowerCommand.TryCreate(limit, original.Get(limit).Milliwatts, out var restore);
            restores.Add(restore);
        }
        OriginalRestores = restores.AsReadOnly();
    }

    internal CpuLiveCurvePeerState? CurvePeer { get; }
    internal CpuLivePowerWriteScope Scope { get; }
    internal CpuLivePowerCommand Command { get; }
    internal CpuLivePowerCommandPurpose Purpose { get; }
    internal CpuLivePowerPair Before { get; }
    internal CpuLivePowerPair After { get; }
    internal CpuLivePowerPair Original { get; }
    internal CpuLivePowerCommand ImmediateReverse { get; }
    internal IReadOnlyList<CpuLivePowerCommand> OriginalRestores { get; }

    private static bool ValidPair(CpuLivePowerPair? pair) => pair is not null
        && CpuLivePowerValue.TryFromRawBits(pair.Fast.RawBits, out var fast) && fast == pair.Fast
        && CpuLivePowerValue.TryFromRawBits(pair.Slow.RawBits, out var slow) && slow == pair.Slow
        && float.IsFinite(BitConverter.UInt32BitsToSingle(pair.StapmRawBits));
}

// Independently admitted, freshly decoded firmware 50001 metadata in mW.
// A raw Min/Max record or Current shadow cannot construct that trust. The proof
// provider must establish units and applicability to both setters. Only the
// retained step1 contract is represented; other granularities need review.
internal sealed record CpuLivePowerTrustedRange(uint Selector, uint MinimumMilliwatts,
    uint MaximumMilliwatts, uint StepMilliwatts)
{
    internal bool AllowsOperation(CpuLivePowerNativeRequest request, uint capability, CpuLivePowerFusedCap cap) =>
        CpuLivePowerCapability.TryDecode(capability) is { } decoded && AllowsCommand(request.Command, decoded, cap)
        && (request.Purpose != CpuLivePowerCommandPurpose.Forward || AllowsCommand(request.ImmediateReverse, decoded, cap))
        && request.OriginalRestores.All(command => AllowsCommand(command, decoded, cap));

    internal bool AllowsCommand(CpuLivePowerCommand command, CpuLivePowerCapability capability, CpuLivePowerFusedCap cap) =>
        Selector == 0x50001 && MinimumMilliwatts <= MaximumMilliwatts
        && MaximumMilliwatts <= CpuLivePowerValue.MaximumMilliwatts && StepMilliwatts == 1
        && command.Milliwatts >= MinimumMilliwatts && command.Milliwatts <= MaximumMilliwatts
        && capability.AllowsUnderFusedCap(command, cap);
}

// No concrete implementation exists. The provider must independently admit
// candidate PM offsets08/10 as the exact requested fast/slow state, including
// integer-mW inversion, clamp behavior, freshness and independent restoration.
// Arithmetic reconstruction only rejects incompatible bytes; a reported
// effective ceiling is not necessarily a restorable requested value.
// It must own the complete original restoration plan and prove loaded-driver,
// boot, AC/session and transition continuity, plus PM refresh/kernel-copy and
// setter authority. Record equality, a bool, query/PM approval or CO proof is
// insufficient. Current must fail closed on observation gaps or cleanup doubt.
internal abstract class CpuLivePowerWriteProof(CpuLivePowerNativeRequest request)
{
    private readonly long created = Stopwatch.GetTimestamp();
    private int consumed;
    internal bool Consume(CpuLivePowerNativeRequest exactRequest) =>
        Interlocked.Exchange(ref consumed, 1) == 0 && ReferenceEquals(request, exactRequest) && Current(exactRequest);
    internal bool Current(CpuLivePowerNativeRequest exactRequest) => ReferenceEquals(request, exactRequest)
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2) && IsCurrent(exactRequest);
    // Exact-request peer restoration admission under this native lease. The
    // adapter verifies the complete expected CO vector; the provider must admit
    // every differing first-original restore using fresh CO range/capability.
    // Never reenter a backend observer or acquire another native lease here.
    internal virtual bool AdmitCurvePeer(CpuLivePowerNativeRequest exactRequest) => false;
    internal abstract bool IsCurrent(CpuLivePowerNativeRequest exactRequest);
    internal abstract CpuLivePowerTrustedRange? ReadFreshRange(CpuLivePowerNativeRequest exactRequest);
}

internal enum CpuLivePowerNativeDisposition { NotSubmitted, PossibleSubmission, Acknowledged }
internal enum CpuLivePowerNativeFailure { None, AdmissionDenied, Transport, Cancelled, ReadbackDrift, Cleanup }
internal sealed record CpuLivePowerNativeResult(CpuLivePowerNativeDisposition Disposition,
    bool WorkerTerminal, bool CleanupComplete, bool Verified, CpuLivePowerSnapshot? After, CpuLivePowerNativeFailure Failure);
