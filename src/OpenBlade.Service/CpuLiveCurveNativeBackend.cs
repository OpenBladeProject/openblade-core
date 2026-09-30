using OpenBlade.Protocol;

namespace OpenBlade.Service;

// No concrete provider exists. Record shape/equality is an additional rejection
// check, never evidence of trusted boot, driver, AC/session or power continuity.
// Implementations must independently validate fresh source/transition evidence,
// strictly decoded CO metadata and the complete original restoration plan. The
// proof must repeat those checks in its native-lease callbacks, including gaps.
internal interface ICpuLiveCurveTrustedNativeProvider
{
    CpuLiveCurveContext? ReadFreshContext(CancellationToken token);
    CpuQueryAttendedApproval? CreateQueryApproval(CpuLiveCurveContext exactContext, CancellationToken token);
    bool CheckWriteAdmission(CpuLiveCurveContext exactContext, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token);
    CpuLiveCurveWriteProof? CreateWriteProof(CpuLiveCurveContext exactContext,
        CpuLiveCurveNativeRequest exactRequest, CancellationToken token);
}

internal enum CpuLiveCurveNativeWorkerKind { None, Observation, Admission, Submission }
internal sealed record CpuLiveCurveNativeBackendStatus(bool WorkerOutstanding,
    CpuLiveCurveNativeWorkerKind WorkerKind, bool ScopeInvalid, CpuLiveCurveNativeResult? LastNativeResult);

// Unwired bridge only: callers supply the same retained typed session and an actual
// trusted provider. Nothing constructs a native platform, creates approval from
// a bool, provisions a driver, establishes ownership or updates a journal here.
internal sealed class CpuLiveCurveNativeBackend : ICpuLiveCurveBackend
{
    private readonly WindowsRz090528CpuNativeSession session;
    private readonly ICpuLiveCurveTrustedNativeProvider provider;
    private readonly CpuLiveCurveWriteScope scope = new();
    private readonly CpuNativeWorkerCoordinator state;

    internal CpuLiveCurveNativeBackend(ICpuQueryWindowsPlatform platform, ICpuLiveCurveTrustedNativeProvider provider)
        : this(WindowsRz090528CpuNativeSession.ForPlatform(platform), provider)
    {
    }

    internal CpuLiveCurveNativeBackend(WindowsRz090528CpuNativeSession session, ICpuLiveCurveTrustedNativeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(provider);
        this.session = session; this.provider = provider;
        state = CpuNativeWorkerCoordinator.ForSession(session);
    }

    // Read-only lifecycle evidence, not journal/restore authority. The task is
    // the actual worker, not the cancellation-sensitive task returned to callers.
    internal Task? OutstandingWorker => state.OutstandingWorker;
    internal CpuLiveCurveNativeBackendStatus Status
    {
        get { var status = state.Status; return new(status.WorkerOutstanding, (CpuLiveCurveNativeWorkerKind)status.Kind, status.Invalid, status.CurveResult); }
    }

    public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token) =>
        Start(CpuLiveCurveNativeWorkerKind.Observation, Observe, new CpuLiveCurveBackendObservation(null, null), token);

    public Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token) => Start(CpuLiveCurveNativeWorkerKind.Admission, workToken =>
        {
            if (!ValidCommand(command, purpose) || !MatchesFresh(context, workToken)) { return false; }
            bool admitted = provider.CheckWriteAdmission(context, command, purpose, workToken);
            workToken.ThrowIfCancellationRequested();
            return admitted && MatchesFresh(context, workToken);
        }, false, token);

    public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token) =>
        Start(CpuLiveCurveNativeWorkerKind.Submission, workToken => Send(context, command, purpose, expectedBefore, workToken),
            CpuLiveCurveSubmission.Unknown, token);

    private CpuLiveCurveBackendObservation Observe(CancellationToken token)
    {
        var context = provider.ReadFreshContext(token);
        if (context is not { IsWellFormed: true }) { return new(null, null); }
        var approval = provider.CreateQueryApproval(context, token);
        if (approval is null || !MatchesFresh(context, token)) { return new(null, null); }
        var result = session.Query(approval, () => ReadBoundUsb(context, token), token);
        if (result.Diagnostic?.Stage == CpuQueryStage.Cleanup) { Invalidate(); }
        token.ThrowIfCancellationRequested();
        if (result.Status != CpuLiveQueryStatus.Observed || CpuLiveCurveSnapshot.TryCreate(result.Observation) is null)
        { return new(null, null); }
        if (!MatchesFresh(context, token)) { Invalidate(); return new(null, null); }
        return new(context, result.Observation);
    }

    private CpuLiveCurveSubmission Send(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token)
    {
        if (!ValidCommand(command, purpose) || !MatchesFresh(context, token)) { return CpuLiveCurveSubmission.NotSubmitted; }
        var request = new CpuLiveCurveNativeRequest(scope, command, purpose, expectedBefore);
        if (!provider.CheckWriteAdmission(context, command, purpose, token) || !MatchesFresh(context, token))
        { return CpuLiveCurveSubmission.NotSubmitted; }
        var proof = provider.CreateWriteProof(context, request, token);
        if (proof is null || !proof.Consume(request) || !MatchesFresh(context, token))
        { return CpuLiveCurveSubmission.NotSubmitted; }
        var boundProof = new ContextBoundProof(request, proof, this, context, token);
        var result = session.ExecuteCurve(request, boundProof, () => ReadBoundUsb(context, token), token);
        state.Record(result);
        if (!result.WorkerTerminal || !result.CleanupComplete) { Invalidate(); return CpuLiveCurveSubmission.Unknown; }
        if (!MatchesFresh(context, token)) { Invalidate(); return CpuLiveCurveSubmission.Unknown; }
        token.ThrowIfCancellationRequested();
        return MapSubmission(request, result);
    }

    internal static CpuLiveCurveSubmission MapSubmission(CpuLiveCurveNativeRequest request, CpuLiveCurveNativeResult result)
    {
        if (!result.WorkerTerminal || !result.CleanupComplete) { return CpuLiveCurveSubmission.Unknown; }
        return result.Disposition switch
        {
            CpuLiveCurveNativeDisposition.NotSubmitted => CpuLiveCurveSubmission.NotSubmitted,
            CpuLiveCurveNativeDisposition.Acknowledged when result.Verified && result.Failure == CpuLiveCurveNativeFailure.None
                && CpuLiveCurveNativeRequest.Equal(result.After, request.After) => CpuLiveCurveSubmission.Acknowledged,
            _ => CpuLiveCurveSubmission.Unknown,
        };
    }

    private bool MatchesFresh(CpuLiveCurveContext? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return expected is { IsWellFormed: true } && provider.ReadFreshContext(token) is { IsWellFormed: true } current
            && current == expected;
    }
    private DeviceIdentity? ReadBoundUsb(CpuLiveCurveContext context, CancellationToken token) =>
        MatchesFresh(context, token) ? context.Identity : null;
    private static bool ValidCommand(CpuLiveCurveCommand command, CpuLiveCurveCommandPurpose purpose) =>
        Enum.IsDefined(purpose) && CpuLiveCurveCommand.TryDecode(CpuLiveCurveCommand.Message, command.EncodeArgument(),
            CpuLiveCurveSnapshot.SupportedMask, out var decoded) && decoded == command
        && (purpose != CpuLiveCurveCommandPurpose.Forward || command.Offset is >= -30 and <= 0);
    private void Invalidate() => state.Invalidate();

    private Task<T> Start<T>(CpuLiveCurveNativeWorkerKind kind, Func<CancellationToken, T> work, T refused, CancellationToken token) =>
        state.Start(CpuNativeWorkerFamily.Curve, (CpuNativeWorkerKind)kind, work, refused, token);
    private sealed class ContextBoundProof(CpuLiveCurveNativeRequest request, CpuLiveCurveWriteProof source,
        CpuLiveCurveNativeBackend owner, CpuLiveCurveContext context, CancellationToken token) : CpuLiveCurveWriteProof(request)
    {
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) =>
            source.Current(exactRequest) && owner.MatchesFresh(context, token);
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
        {
            if (!IsCurrent(exactRequest)) { return null; }
            var range = source.ReadFreshRange(exactRequest);
            return IsCurrent(exactRequest) ? range : null;
        }
    }
}
