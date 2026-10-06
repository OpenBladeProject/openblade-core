using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Record shape/equality is an additional rejection check. Providers bind fresh
// runtime admission to a retained native device lifetime.
// Implementations must independently validate fresh source/transition evidence,
// strictly decoded CO metadata and the complete original restoration plan. The
// proof must repeat those checks in its native-lease callbacks, including gaps.
internal interface ICpuLiveCurveTrustedNativeProvider
{
    CpuSavedAdmissionDiagnostic? Diagnostic => null;
    bool SupportsReadPreparation => false;
    CpuLiveCurveWriteDeadline? BeginSubmission(CancellationToken token) => null;
    CpuLiveCurveWriteProof? CreatePreparedWriteProof(CpuLiveCurveContext exactContext,
        CpuLiveCurveNativeRequest exactRequest, CancellationToken token) => null;
    IDisposable? BeginGetter(CancellationToken token) => null;
    CpuLiveCurveContext? ReadFreshContext(CancellationToken token);
    bool CompleteGetter(CpuLiveCurveContext expected, CancellationToken token) =>
        ReadFreshContext(token) is { } actual && expected.MatchesHardwareContext(actual);
    CpuQueryInstalledApproval? CreateInstalledQueryApproval(CpuLiveCurveContext exactContext, CancellationToken token) => null;
    bool CheckWriteAdmission(CpuLiveCurveContext exactContext, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token);
    bool CheckPlanAdmission(CpuLiveCurveContext exactContext, IReadOnlyList<CpuLiveCurveAdmissionCheck> checks, CancellationToken token)
    {
        foreach (var check in checks)
        {
            token.ThrowIfCancellationRequested();
            if (!CheckWriteAdmission(exactContext, check.Command, check.Purpose, token)) { return false; }
        }
        return !token.IsCancellationRequested;
    }
    CpuLiveCurveWriteProof? CreateWriteProof(CpuLiveCurveContext exactContext,
        CpuLiveCurveNativeRequest exactRequest, CancellationToken token);
}

internal enum CpuLiveCurveNativeWorkerKind { None, Observation, Admission, Submission }
internal sealed record CpuLiveCurveNativeBackendStatus(bool WorkerOutstanding,
    CpuLiveCurveNativeWorkerKind WorkerKind, bool ScopeInvalid, CpuLiveCurveNativeResult? LastNativeResult);

// An exact prepared read may prove its request and expected hardware/runtime
// context together at this checkpoint. Null retains the independent fallback;
// false is a denial, never permission to try another admission path.
internal interface ICpuLiveCurveContextBoundReadProof
{
    bool? ReadCurrentWithContext(CpuLiveCurveNativeRequest request, CpuLiveCurveContext expected);
}

// Callers supply the same retained typed session and an actual
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
    public CpuSavedAdmissionDiagnostic? Diagnostic => provider.Diagnostic;
    internal CpuLiveCurveNativeBackendStatus Status
    {
        get { var status = state.Status; return new(status.WorkerOutstanding, (CpuLiveCurveNativeWorkerKind)status.Kind, status.Invalid, status.CurveResult); }
    }

    public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token) => ObserveAsync(token, null);
    public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic) =>
        state.Start(CpuNativeWorkerFamily.Curve, CpuNativeWorkerKind.Observation,
            workToken => Observe(workToken, diagnostic), new CpuLiveCurveBackendObservation(null, null), token, diagnostic);

    public Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token) => Start(CpuLiveCurveNativeWorkerKind.Admission, workToken =>
        {
            if (!ValidCommand(command, purpose) || !MatchesFresh(context, workToken)) { return false; }
            bool admitted = provider.CheckWriteAdmission(context, command, purpose, workToken);
            workToken.ThrowIfCancellationRequested();
            return admitted && MatchesFresh(context, workToken);
        }, false, token);

    public Task<bool> CheckPlanAdmissionAsync(CpuLiveCurveContext context,
        IReadOnlyList<CpuLiveCurveAdmissionCheck> checks, CancellationToken token)
    {
        if (checks.Count is < 1 or > 36) { return Task.FromResult(false); }
        var exact = checks.ToArray();
        return Start(CpuLiveCurveNativeWorkerKind.Admission, workToken =>
        {
            using var stage = Diagnostic?.BeginStage("curve complete-plan preflight");
            if (exact.Any(check => !ValidCommand(check.Command, check.Purpose)) || !MatchesFresh(context, workToken)) { return false; }
            return provider.CheckPlanAdmission(context, exact, workToken) && MatchesFresh(context, workToken);
        }, false, token);
    }

    public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token) =>
        SendAsync(context, command, purpose, expectedBefore, null, token);

    public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CpuLivePowerPeerState? peer, CancellationToken token) =>
        state.Start(CpuNativeWorkerFamily.Curve, CpuNativeWorkerKind.Submission,
            workToken => Send(context, command, purpose, expectedBefore, peer, workToken),
            CpuLiveCurveSubmission.Unknown, token, Diagnostic, returnTerminal: true);

    private CpuLiveCurveBackendObservation Observe(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic)
    {
        bool Check(bool allowed, string stage) => diagnostic?.Check(allowed, stage) ?? allowed;
        using var getter = provider.BeginGetter(token);
        var context = provider.ReadFreshContext(token);
        if (!Check(context is { IsWellFormed: true }, "native curve context unavailable")) { return new(null, null); }
        var installed = provider.CreateInstalledQueryApproval(context!, token);
        if (!Check(installed is not null, "native curve query approval")
            || !Check(MatchesFresh(context!, token), "native curve context before query")) { return new(null, null); }
        Rz090528CpuLiveQueryResult result;
        CpuQueryProgress? progress = null;
        using (diagnostic?.BeginStage("native curve query (admission/mutex/mailbox)"))
        {
            result = session.QueryInstalled(installed, () => ReadBoundUsb(context!, token), token, progress = diagnostic?.BeginNativeQuery());
        }
        if (result.Diagnostic is { } detail)
        {
            diagnostic?.Check(false, $"native query {detail.Stage}/{detail.Failure}; native error {detail.NativeErrorCode}; operation {detail.Operation}"
                + progress?.Snapshot(freeze: true));
        }
        if (result.Diagnostic?.Stage == CpuQueryStage.Cleanup) { Invalidate(); }
        token.ThrowIfCancellationRequested();
        if (!Check(result.Status == CpuLiveQueryStatus.Observed, "native curve query status " + result.Status)
            || !Check(CpuLiveCurveSnapshot.TryCreate(result.Observation) is not null, "native curve snapshot invalid"))
        { return new(null, null); }
        if (!Check(provider.CompleteGetter(context!, token), "native curve context after query")) { Invalidate(); return new(null, null); }
        return new(context, result.Observation);
    }

    private CpuLiveCurveSubmission Send(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CpuLivePowerPeerState? peer, CancellationToken token)
    {
        var diagnostic = Diagnostic;
        bool Check(bool allowed, string name) => diagnostic?.Check(allowed, name) ?? allowed;
        using var deadline = provider.BeginSubmission(token);
        if (deadline is not null)
        {
            if (!Check(deadline.IsCurrent(), "validation write deadline")) { return CpuLiveCurveSubmission.NotSubmitted; }
            token = deadline.Token;
        }
        using var preparation = diagnostic?.BeginStage("curve send proof preparation");
        if (!Check(ValidCommand(command, purpose), "curve send command shape")) { return CpuLiveCurveSubmission.NotSubmitted; }
        var request = new CpuLiveCurveNativeRequest(scope, command, purpose, expectedBefore, peer);
        CpuLiveCurveWriteProof? CreateProof()
        {
            if (provider.SupportsReadPreparation) { return provider.CreatePreparedWriteProof(context, request, token); }
            if (!Check(MatchesFresh(context, token), "curve send initial context")
                || !Check(provider.CheckWriteAdmission(context, command, purpose, token), "curve send write admission")
                || !Check(MatchesFresh(context, token), "curve send admitted context")) { return null; }
            return provider.CreateWriteProof(context, request, token);
        }
        // Keep preparation rooted in the actual worker, including failures before
        // Protocol adopts it. A timed-out caller must not dispose this resource.
        using var proof = CreateProof();
        if (!Check(proof is not null, "curve send proof creation")
            || !Check(proof!.Consume(request), "curve send proof consumption")
            || !Check(MatchesFresh(context, token), "curve send proof context"))
        { return CpuLiveCurveSubmission.NotSubmitted; }
        var boundProof = new ContextBoundProof(request, proof!, this, context, token, deadline);
        // ReadFreshContext uses the provider's exact active getter continuity
        // during preparation/readback, and full context outside that scope.
        CpuLiveCurveNativeResult result;
        using (diagnostic?.BeginStage("curve native execution (preparation/setter/readback)"))
        { result = session.ExecuteCurve(request, boundProof, () => ReadBoundUsb(context, token), token); }
        state.Record(result);
        if (result.Failure != CpuLiveCurveNativeFailure.None)
        { diagnostic?.Check(false, "curve native result " + result.Disposition + "/" + result.Failure); }
        if (!result.WorkerTerminal || !result.CleanupComplete) { Invalidate(); return CpuLiveCurveSubmission.Unknown; }
        // Terminal non-submission is historical evidence, not fresh write
        // authority. Cancellation or context drift must not discard it.
        if (result.Disposition == CpuLiveCurveNativeDisposition.NotSubmitted)
        { Invalidate(); return CpuLiveCurveSubmission.NotSubmitted; }
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
            && expected.MatchesHardwareContext(current);
    }
    private DeviceIdentity? ReadBoundUsb(CpuLiveCurveContext context, CancellationToken token) =>
        MatchesFresh(context, token) ? context.Identity : null;
    private static bool ValidCommand(CpuLiveCurveCommand command, CpuLiveCurveCommandPurpose purpose) =>
        Enum.IsDefined(purpose) && CpuLiveCurveCommand.TryDecode(CpuLiveCurveCommand.Message, command.EncodeArgument(),
            CpuLiveCurveSnapshot.SupportedMask, out var decoded) && decoded == command
        && (purpose != CpuLiveCurveCommandPurpose.Forward || command.Offset is >= -30 and <= 0);
    private void Invalidate() => state.Invalidate();

    private Task<T> Start<T>(CpuLiveCurveNativeWorkerKind kind, Func<CancellationToken, T> work, T refused, CancellationToken token) =>
        state.Start(CpuNativeWorkerFamily.Curve, (CpuNativeWorkerKind)kind, work, refused, token, Diagnostic);
    internal sealed class ContextBoundProof(CpuLiveCurveNativeRequest request, CpuLiveCurveWriteProof source,
        CpuLiveCurveNativeBackend owner, CpuLiveCurveContext context, CancellationToken token,
        CpuLiveCurveWriteDeadline? deadline = null) : CpuLiveCurveWriteProof(request)
    {
        internal override bool IsSubmissionCurrent(CpuLiveCurveNativeRequest exactRequest) =>
            source.SubmissionCurrent(exactRequest) && !token.IsCancellationRequested && (deadline?.IsCurrent() ?? true);
        internal override bool IsPreparationCurrent(CpuLiveCurveNativeRequest exactRequest) =>
            source.PreparationCurrent(exactRequest) && owner.MatchesFresh(context, token);
        internal override IDisposable? BeginReadCore(CpuLiveCurveNativeRequest exactRequest) => source.BeginRead(exactRequest);
        internal override bool IsReadCurrent(CpuLiveCurveNativeRequest exactRequest)
        {
            token.ThrowIfCancellationRequested();
            if (source is ICpuLiveCurveContextBoundReadProof bound
                && bound.ReadCurrentWithContext(exactRequest, context) is bool current)
            {
                token.ThrowIfCancellationRequested();
                return current;
            }
            return source.ReadCurrent(exactRequest) && owner.MatchesFresh(context, token);
        }
        internal override bool CompleteReadCore(CpuLiveCurveNativeRequest exactRequest) =>
            source.CompleteRead(exactRequest) && owner.MatchesFresh(context, token);
        internal override bool AdmitPowerPeer(CpuLiveCurveNativeRequest exactRequest, uint capability, CpuLivePowerFusedCap cap) =>
            IsReadCurrent(exactRequest) && source.AdmitPowerPeer(exactRequest, capability, cap) && IsReadCurrent(exactRequest);
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) =>
            source.Current(exactRequest) && owner.MatchesFresh(context, token);
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
        {
            if (!IsReadCurrent(exactRequest)) { return null; }
            var range = source.ReadFreshRange(exactRequest);
            return IsReadCurrent(exactRequest) ? range : null;
        }
    }
}
