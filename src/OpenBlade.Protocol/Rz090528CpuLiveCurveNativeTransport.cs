using System.Diagnostics;

namespace OpenBlade.Protocol;

// Correlation only. A scope object never proves boot, process, driver or power continuity.
internal sealed class CpuLiveCurveWriteScope;

internal sealed class CpuLiveCurveNativeRequest
{
    internal CpuLiveCurveNativeRequest(CpuLiveCurveWriteScope scope, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(expectedBefore);
        if (!Enum.IsDefined(purpose)
            || !CpuLiveCurveCommand.TryDecode(CpuLiveCurveCommand.Message, command.EncodeArgument(),
                CpuLiveCurveSnapshot.SupportedMask, out var decoded) || decoded != command
            || purpose == CpuLiveCurveCommandPurpose.Forward && command.Offset is < -30 or > 0)
        { throw new ArgumentException("Invalid exact curve operation."); }
        Scope = scope; Command = command; Purpose = purpose; Before = expectedBefore;
        After = expectedBefore.WithOffset(command);
        short previous = expectedBefore.CoreOffsets.Single(core => core.PhysicalCoreId == command.PhysicalCoreId).Offset;
        if (!CpuLiveCurveCommand.TryCreate(command.PhysicalCoreId, previous,
            CpuLiveCurveSnapshot.SupportedMask, out var reverse))
        { throw new ArgumentException("Invalid exact reverse operation."); }
        ImmediateReverse = reverse;
    }

    internal CpuLiveCurveWriteScope Scope { get; }
    internal CpuLiveCurveCommand Command { get; }
    internal CpuLiveCurveCommand ImmediateReverse { get; }
    internal CpuLiveCurveCommandPurpose Purpose { get; }
    internal CpuLiveCurveSnapshot Before { get; }
    internal CpuLiveCurveSnapshot After { get; }
    internal static bool Equal(CpuLiveCurveSnapshot? left, CpuLiveCurveSnapshot? right) =>
        left is not null && right is not null && left.CoreOffsets.SequenceEqual(right.CoreOffsets);
}

// Independently obtained fresh metadata for CO selector5000A, in NEGATED offset
// units, matching the vendor setter. Current is deliberately absent: it is not
// live CO. This slice only admits the retained 0..50/step1 contract (or a narrower
// interval); broad signed16 representability does not grant write permission.
internal sealed record CpuLiveCurveTrustedRange(uint Selector, int MinimumMagnitude,
    int MaximumMagnitude, int Step, bool Supported)
{
    // Forward must be reversible to the exact immediately preceding value.
    // A restore checks its actual target; reversing that restore would reapply
    // the edited state and is not restoration authority. Whole original-plan
    // admission across successive edits still belongs to the trusted proof.
    internal bool AllowsOperation(CpuLiveCurveNativeRequest request) => Allows(request.Command)
        && (request.Purpose == CpuLiveCurveCommandPurpose.Restore || Allows(request.ImmediateReverse));

    internal bool Allows(CpuLiveCurveCommand command) => Supported && Selector == 0x5000A
        && MinimumMagnitude >= 0 && MaximumMagnitude <= 50 && MinimumMagnitude <= MaximumMagnitude
        && Step == 1 && -(int)command.Offset >= MinimumMagnitude && -(int)command.Offset <= MaximumMagnitude;
}

// No runtime implementation exists in this slice. The eventual trusted provider
// must bind this exact immutable request/scope to independently admitted forward
// AND reverse authority, loaded-driver lifetime, transition/AC/session continuity,
// deadline, source/physical admission and fresh, strictly decoded CO metadata.
// A getter approval, historical metadata or caller-supplied bool cannot implement
// that trust. Only offline fakes supply this contract today.
internal abstract class CpuLiveCurveWriteProof(CpuLiveCurveNativeRequest request)
{
    private readonly long created = Stopwatch.GetTimestamp();
    private int consumed;
    internal bool Consume(CpuLiveCurveNativeRequest exactRequest) =>
        Interlocked.Exchange(ref consumed, 1) == 0 && ReferenceEquals(request, exactRequest) && Current(exactRequest);
    internal bool Current(CpuLiveCurveNativeRequest exactRequest) => ReferenceEquals(request, exactRequest)
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2) && IsCurrent(exactRequest);
    internal abstract bool IsCurrent(CpuLiveCurveNativeRequest exactRequest);
    internal abstract CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest);
}

internal enum CpuLiveCurveNativeDisposition { NotSubmitted, PossibleSubmission, Acknowledged }
internal enum CpuLiveCurveNativeFailure { None, AdmissionDenied, Transport, Cancelled, ReadbackDrift, Cleanup }
internal sealed record CpuLiveCurveNativeResult(CpuLiveCurveNativeDisposition Disposition,
    bool WorkerTerminal, bool CleanupComplete, bool Verified, CpuLiveCurveSnapshot? After,
    CpuLiveCurveNativeFailure Failure);
