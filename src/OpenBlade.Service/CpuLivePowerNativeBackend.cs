using OpenBlade.Protocol;

namespace OpenBlade.Service;

// No concrete provider exists. These callbacks independently establish fresh
// source/continuity, PM requested-state interpretation, range units/applicability
// and exact restoration ownership. Candidate bytes and equality only reject.
internal interface ICpuLivePowerTrustedNativeProvider
{
    CpuLivePowerContext? ReadFreshContext(CancellationToken token);
    CpuPmAttendedApproval? CreateObservationApproval(CpuLivePowerContext exactContext, CancellationToken token);
    CpuPmRunOwnedDriverLifetime? ReadFreshObservationLifetime(CpuLivePowerContext exactContext, CancellationToken token);
    // Admit the exact sample as requested/restorable state, not an effective
    // ceiling, and independently establish the metadata's mW/step1 contract.
    CpuLivePowerTrustedRange? AdmitRequestedStateAndRange(CpuLivePowerContext exactContext,
        CpuPmObservationBatch exactObservation, CancellationToken token);
    bool CheckWriteAdmission(CpuLivePowerContext exactContext, CpuLivePowerCommand command,
        CpuLivePowerCommandPurpose purpose, CpuLivePowerPair expectedBefore, CpuLivePowerPair original, CancellationToken token);
    CpuLivePowerWriteProof? CreateWriteProof(CpuLivePowerContext exactContext, CpuLivePowerNativeRequest exactRequest, CancellationToken token);
}

internal sealed record CpuLivePowerNativeBackendStatus(bool WorkerOutstanding, CpuNativeWorkerKind WorkerKind,
    bool ScopeInvalid, CpuLivePowerNativeResult? LastNativeResult);

// Unwired bridge. Observation retains the existing attended approval/lifetime
// gates; this is not production observation authority for an installed driver.
internal sealed class CpuLivePowerNativeBackend : ICpuLivePowerBackend
{
    private readonly WindowsRz090528CpuNativeSession session;
    private readonly ICpuLivePowerTrustedNativeProvider provider;
    private readonly IRz090528CpuLivePowerRangeMetadataReader metadataReader;
    private readonly CpuNativeWorkerCoordinator state;

    internal CpuLivePowerNativeBackend(ICpuQueryWindowsPlatform platform, ICpuLivePowerTrustedNativeProvider provider,
        IRz090528CpuLivePowerRangeMetadataReader metadataReader)
        : this(WindowsRz090528CpuNativeSession.ForPlatform(platform), provider, metadataReader) { }
    internal CpuLivePowerNativeBackend(WindowsRz090528CpuNativeSession session, ICpuLivePowerTrustedNativeProvider provider,
        IRz090528CpuLivePowerRangeMetadataReader metadataReader)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(metadataReader);
        this.session = session; this.provider = provider; this.metadataReader = metadataReader;
        state = CpuNativeWorkerCoordinator.ForSession(session);
    }
    internal Task? OutstandingWorker => state.OutstandingWorker;
    internal CpuLivePowerNativeBackendStatus Status
    { get { var status = state.Status; return new(status.WorkerOutstanding, status.Kind, status.Invalid, status.PowerResult); } }
    public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token) =>
        state.Start(CpuNativeWorkerFamily.Power, CpuNativeWorkerKind.Observation, Observe, Empty(), token);
    public Task<bool> CheckWriteAdmissionAsync(CpuLivePowerContext context, CpuLivePowerCommand command,
        CpuLivePowerCommandPurpose purpose, CpuLivePowerPair expectedBefore, CpuLivePowerPair original, CancellationToken token) =>
        state.Start(CpuNativeWorkerFamily.Power, CpuNativeWorkerKind.Admission, workToken =>
        {
            if (!ValidCommand(command, purpose) || !ValidPair(expectedBefore) || !ValidPair(original)
                || expectedBefore.StapmRawBits != original.StapmRawBits || !MatchesFresh(context, workToken)) { return false; }
            bool admitted = provider.CheckWriteAdmission(context, command, purpose, expectedBefore, original, workToken);
            workToken.ThrowIfCancellationRequested(); return admitted && MatchesFresh(context, workToken);
        }, false, token);
    public Task<CpuLivePowerSubmission> SendAsync(CpuLivePowerContext context, CpuLivePowerNativeRequest request, CancellationToken token) =>
        state.Start(CpuNativeWorkerFamily.Power, CpuNativeWorkerKind.Submission, workToken => Send(context, request, workToken), CpuLivePowerSubmission.Unknown, token);

    private CpuLivePowerBackendObservation Observe(CancellationToken token)
    {
        var context = provider.ReadFreshContext(token);
        if (context is not { IsWellFormed: true }) { return Empty(); }
        var approval = provider.CreateObservationApproval(context, token);
        if (approval?.Profile != CpuPmObservationProfile.RangeSingleSample || !MatchesFresh(context, token)) { return Empty(); }
        var observed = session.ObservePower(approval, () => MatchesFresh(context, token)
            ? provider.ReadFreshObservationLifetime(context, token) : null, metadataReader, token);
        if (!observed.WorkerTerminal || !observed.CleanupComplete) { state.Invalidate(); return Empty(); }
        token.ThrowIfCancellationRequested();
        var batch = observed.Batch; var samples = batch.CopySamples();
        if (!batch.Complete || batch.Profile != CpuPmObservationProfile.RangeSingleSample || samples.Length != 1
            || CpuLivePowerSnapshot.TryCreate(samples[0].Snapshot) is null || batch.FusedCap?.Candidate is not { } cap
            || batch.RangeObservation is not
            {
                MetadataComplete: true, Capability:
                {
                    Command: 0x82, Argument: 0,
                    InvocationEntered: true, ResponseAcknowledged: true, ReturnedWordTransportSucceeded: true,
                    PostReadAdmissionPassed: true, RawValue: uint rawCapability
                }
            }
            || CpuLivePowerCapability.TryDecode(rawCapability) is not { } capability) { return Empty(); }
        if (!MatchesFresh(context, token)) { state.Invalidate(); return Empty(); }
        var range = provider.AdmitRequestedStateAndRange(context, batch, token);
        token.ThrowIfCancellationRequested();
        if (!MatchesFresh(context, token)) { state.Invalidate(); return Empty(); }
        if (range is not { Selector: 0x50001, StepMilliwatts: 1 } || range.MinimumMilliwatts > range.MaximumMilliwatts
            || range.MaximumMilliwatts > CpuLivePowerValue.MaximumMilliwatts) { return Empty(); }
        return new(context, samples[0].Snapshot, cap, capability, range);
    }

    private CpuLivePowerSubmission Send(CpuLivePowerContext context, CpuLivePowerNativeRequest request, CancellationToken token)
    {
        if (request is null || !MatchesFresh(context, token)) { return CpuLivePowerSubmission.NotSubmitted; }
        // Preserve the caller's exact reference and durable first Original. No
        // rebinding to a backend scope or substitution of the current Before.
        if (!Admit(request.Command, request.Purpose)
            || request.Purpose == CpuLivePowerCommandPurpose.Forward && !Admit(request.ImmediateReverse, CpuLivePowerCommandPurpose.Restore))
        { return CpuLivePowerSubmission.NotSubmitted; }
        foreach (var original in request.OriginalRestores)
        { if (!Admit(original, CpuLivePowerCommandPurpose.Restore)) { return CpuLivePowerSubmission.NotSubmitted; } }
        var proof = provider.CreateWriteProof(context, request, token);
        if (proof is null || !proof.Consume(request) || !MatchesFresh(context, token)) { return CpuLivePowerSubmission.NotSubmitted; }
        var bound = new ContextBoundProof(request, proof, this, context, token);
        var result = session.ExecutePower(request, bound, () => MatchesFresh(context, token) ? context.Identity : null, token);
        state.Record(result);
        if (!result.WorkerTerminal || !result.CleanupComplete) { state.Invalidate(); return CpuLivePowerSubmission.Unknown; }
        if (!MatchesFresh(context, token)) { state.Invalidate(); return CpuLivePowerSubmission.Unknown; }
        token.ThrowIfCancellationRequested(); return MapSubmission(request, result);

        bool Admit(CpuLivePowerCommand command, CpuLivePowerCommandPurpose purpose)
        {
            if (!MatchesFresh(context, token)) { return false; }
            bool admitted = provider.CheckWriteAdmission(context, command, purpose, request.Before, request.Original, token);
            token.ThrowIfCancellationRequested(); return admitted && MatchesFresh(context, token);
        }
    }

    internal static CpuLivePowerSubmission MapSubmission(CpuLivePowerNativeRequest request, CpuLivePowerNativeResult result)
    {
        if (!result.WorkerTerminal || !result.CleanupComplete || result.Failure == CpuLivePowerNativeFailure.Cleanup)
        { return CpuLivePowerSubmission.Unknown; }
        return result.Disposition switch
        {
            CpuLivePowerNativeDisposition.NotSubmitted when !result.Verified => CpuLivePowerSubmission.NotSubmitted,
            CpuLivePowerNativeDisposition.Acknowledged when result.Verified && result.Failure == CpuLivePowerNativeFailure.None
                && result.After?.Pair == request.After => CpuLivePowerSubmission.Acknowledged,
            _ => CpuLivePowerSubmission.Unknown,
        };
    }
    private bool MatchesFresh(CpuLivePowerContext? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return expected is { IsWellFormed: true } && provider.ReadFreshContext(token) is { IsWellFormed: true } current && expected.MatchesHardwareContext(current);
    }
    private static bool ValidPair(CpuLivePowerPair? pair) => pair is not null
        && CpuLivePowerValue.TryFromRawBits(pair.Fast.RawBits, out var fast) && fast == pair.Fast
        && CpuLivePowerValue.TryFromRawBits(pair.Slow.RawBits, out var slow) && slow == pair.Slow
        && float.IsFinite(BitConverter.UInt32BitsToSingle(pair.StapmRawBits));
    private static bool ValidCommand(CpuLivePowerCommand command, CpuLivePowerCommandPurpose purpose) => Enum.IsDefined(purpose)
        && CpuLivePowerCommand.TryCreate(command.Limit, command.Milliwatts, out var decoded) && decoded == command
        && (purpose != CpuLivePowerCommandPurpose.Forward || command.Milliwatts is >= 45000 and <= 95000 && command.Milliwatts % 1000 == 0);
    private static CpuLivePowerBackendObservation Empty() => new(null, null, null, null, null);
    private sealed class ContextBoundProof(CpuLivePowerNativeRequest request, CpuLivePowerWriteProof source,
        CpuLivePowerNativeBackend owner, CpuLivePowerContext context, CancellationToken token) : CpuLivePowerWriteProof(request)
    {
        internal override bool IsCurrent(CpuLivePowerNativeRequest exactRequest) => source.Current(exactRequest) && owner.MatchesFresh(context, token);
        internal override CpuLivePowerTrustedRange? ReadFreshRange(CpuLivePowerNativeRequest exactRequest)
        {
            if (!IsCurrent(exactRequest)) { return null; }
            var range = source.ReadFreshRange(exactRequest);
            return IsCurrent(exactRequest) ? range : null;
        }
    }
}
