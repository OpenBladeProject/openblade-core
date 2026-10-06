using System.Diagnostics;

namespace OpenBlade.Protocol;

// Correlation only. A scope object never proves boot, process, driver or power continuity.
internal sealed class CpuLiveCurveWriteScope;

internal sealed class CpuLiveCurveNativeRequest
{
    internal CpuLiveCurveNativeRequest(CpuLiveCurveWriteScope scope, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CpuLivePowerPeerState? powerPeer = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(expectedBefore);
        if (!Enum.IsDefined(purpose)
            || !CpuLiveCurveCommand.TryDecode(CpuLiveCurveCommand.Message, command.EncodeArgument(),
                CpuLiveCurveSnapshot.SupportedMask, out var decoded) || decoded != command
            || purpose == CpuLiveCurveCommandPurpose.Forward && command.Offset is < -30 or > 0)
        { throw new ArgumentException("Invalid exact curve operation."); }
        Scope = scope; Command = command; Purpose = purpose; Before = expectedBefore; PowerPeer = powerPeer;
        After = expectedBefore.WithOffset(command);
        short previous = expectedBefore.CoreOffsets.Single(core => core.PhysicalCoreId == command.PhysicalCoreId).Offset;
        if (!CpuLiveCurveCommand.TryCreate(command.PhysicalCoreId, previous,
            CpuLiveCurveSnapshot.SupportedMask, out var reverse))
        { throw new ArgumentException("Invalid exact reverse operation."); }
        ImmediateReverse = reverse;
    }

    internal CpuLivePowerPeerState? PowerPeer { get; }
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

// A trusted provider must bind this exact immutable request/scope to independently admitted forward
// AND reverse authority, loaded-driver lifetime, transition/AC/session continuity,
// deadline, source/physical admission and fresh, strictly decoded CO metadata.
// A getter approval, historical metadata or caller-supplied bool cannot implement
// that trust. The installed provider supplies CO authority from reviewed evidence;
// peer PPT restoration remains independently gated.
internal abstract class CpuLiveCurveWriteProof(CpuLiveCurveNativeRequest request) : IDisposable
{
    private readonly long created = Stopwatch.GetTimestamp();
    private int consumed;
    private int disposed;
    private bool admitted;
    internal bool Consume(CpuLiveCurveNativeRequest exactRequest)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !PreparationCurrent(exactRequest)) { return false; }
        admitted = true;
        return true;
    }
    internal bool Current(CpuLiveCurveNativeRequest exactRequest) => Volatile.Read(ref disposed) == 0 && ReferenceEquals(request, exactRequest)
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2) && IsCurrent(exactRequest);
    // Consumption only binds a single exact operation. Production may retain
    // non-authorizing read preparation here; the setter still requires Current.
    internal bool PreparationCurrent(CpuLiveCurveNativeRequest exactRequest) => Volatile.Read(ref disposed) == 0
        && ReferenceEquals(request, exactRequest) && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2)
        && IsPreparationCurrent(exactRequest);
    internal virtual bool IsPreparationCurrent(CpuLiveCurveNativeRequest exactRequest) => IsCurrent(exactRequest);
    // Cheap final lifetime/deadline check AFTER the full fresh setter proof.
    internal bool SubmissionCurrent(CpuLiveCurveNativeRequest exactRequest) => Volatile.Read(ref disposed) == 0
        && admitted && ReferenceEquals(request, exactRequest) && IsSubmissionCurrent(exactRequest);
    internal virtual bool IsSubmissionCurrent(CpuLiveCurveNativeRequest exactRequest) => true;
    // Optional read continuity for this consumed operation only. Neither these
    // checks nor their scope grant setter authority; Current remains independent.
    internal IDisposable? BeginRead(CpuLiveCurveNativeRequest exactRequest) =>
        Volatile.Read(ref disposed) == 0 && admitted && ReferenceEquals(request, exactRequest) && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2)
            ? BeginReadCore(exactRequest) : throw new InvalidOperationException("Invalid consumed CPU read scope.");
    internal bool ReadCurrent(CpuLiveCurveNativeRequest exactRequest) => Volatile.Read(ref disposed) == 0 && admitted && ReferenceEquals(request, exactRequest)
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2) && IsReadCurrent(exactRequest);
    internal bool CompleteRead(CpuLiveCurveNativeRequest exactRequest) => Volatile.Read(ref disposed) == 0 && admitted && ReferenceEquals(request, exactRequest)
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2) && CompleteReadCore(exactRequest);
    internal virtual IDisposable? BeginReadCore(CpuLiveCurveNativeRequest exactRequest) => null;
    internal virtual bool IsReadCurrent(CpuLiveCurveNativeRequest exactRequest) => IsCurrent(exactRequest);
    internal virtual bool CompleteReadCore(CpuLiveCurveNativeRequest exactRequest) => IsCurrent(exactRequest);
    // Optional joint final barrier. It must freshly bind full write authority,
    // context, this operation's range and peer; no result survives the call.
    internal virtual bool SupportsFinalCheckpoint => false;
    internal bool FinalCheckpointCurrent(CpuLiveCurveNativeRequest exactRequest, uint capability, CpuLivePowerFusedCap cap) =>
        Volatile.Read(ref disposed) == 0 && admitted && ReferenceEquals(request, exactRequest)
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2)
        && IsFinalCheckpointCurrent(exactRequest, capability, cap);
    internal virtual bool IsFinalCheckpointCurrent(CpuLiveCurveNativeRequest exactRequest, uint capability, CpuLivePowerFusedCap cap) => false;
    // Called under the initiating native lease, before the peer sample and at
    // the final setter boundary. Independently admit requested-state semantics
    // and every differing first-original restore against fresh range/caps.
    // It must not invoke a backend observer or acquire another native lease.
    internal virtual bool AdmitPowerPeer(CpuLiveCurveNativeRequest exactRequest, uint capability, CpuLivePowerFusedCap cap) => false;
    internal abstract bool IsCurrent(CpuLiveCurveNativeRequest exactRequest);
    internal abstract CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) { DisposeCore(); }
    }
    internal virtual void DisposeCore() { }
}

internal enum CpuLiveCurveNativeDisposition { NotSubmitted, PossibleSubmission, Acknowledged }
internal enum CpuLiveCurveNativeFailure { None, AdmissionDenied, Transport, Cancelled, ReadbackDrift, Cleanup }
internal sealed record CpuLiveCurveNativeResult(CpuLiveCurveNativeDisposition Disposition,
    bool WorkerTerminal, bool CleanupComplete, bool Verified, CpuLiveCurveSnapshot? After,
    CpuLiveCurveNativeFailure Failure);
