using OpenBlade.Protocol;

namespace OpenBlade.Service;

// The production provider shares the retained installed backend with live CO.
// These callbacks independently establish fresh
// source/continuity, PM requested-state interpretation and range units.
// Candidate bytes and equality only reject; no PPT write authority exists.
internal interface ICpuLivePowerTrustedNativeProvider
{
    IDisposable? BeginGetter(CancellationToken token) => null;
    bool CompleteGetter(CpuLivePowerContext expected, CancellationToken token) =>
        expected.MatchesHardwareContext(ReadFreshContext(token));
    CpuLivePowerContext? ReadFreshContext(CancellationToken token);
    CpuPmInstalledApproval? CreateInstalledObservationApproval(CpuLivePowerContext exactContext, CancellationToken token) => null;
    IRz090528CpuLivePowerRangeMetadataReader? CreateObservationMetadata(CpuLivePowerContext exactContext, CancellationToken token) => null;
    // Admit the exact sample as requested/restorable state, not an effective
    // ceiling, and independently establish the metadata's mW/step1 contract.
    CpuLivePowerTrustedRange? AdmitRequestedStateAndRange(CpuLivePowerContext exactContext,
        CpuPmObservationBatch exactObservation, CancellationToken token);
}

internal sealed record CpuLivePowerNativeBackendStatus(bool WorkerOutstanding, CpuNativeWorkerKind WorkerKind, bool ScopeInvalid);

// A terminal empty-window observation can propose one new getter. The anchor
// is immutable; separate one-use gates bind consumption to the controller and
// dispatch to the exact native backend. It grants no setter or reusable proof.
internal sealed class CpuLivePowerRecoveryTicket(CpuLivePowerNativeBackend owner, CpuLivePowerContext context,
    IDisposable getter, CpuPmInstalledApproval approval, IRz090528CpuLivePowerRangeMetadataReader metadata,
    CancellationToken token, string detail)
{
    private int consumed, dispatched;
    internal string Detail { get; } = detail;
    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    internal bool TryDispatch(CpuLivePowerNativeBackend candidate) => ReferenceEquals(owner, candidate)
        && Volatile.Read(ref consumed) == 1 && Interlocked.CompareExchange(ref dispatched, 1, 0) == 0;
    internal bool Matches(CpuLivePowerContext candidate, IDisposable? nextGetter, CpuPmInstalledApproval? nextApproval,
        IRz090528CpuLivePowerRangeMetadataReader? nextMetadata, CancellationToken nextToken) =>
        context.MatchesHardwareContext(candidate) && nextGetter is not null && !ReferenceEquals(getter, nextGetter)
        && nextApproval is not null && !ReferenceEquals(approval, nextApproval) && ReferenceEquals(approval.Lease, nextApproval.Lease)
        && nextMetadata is not null && !ReferenceEquals(metadata, nextMetadata) && token != nextToken;
}

// Observation requires the typed retained-installed approval shared with CO.
internal sealed class CpuLivePowerNativeBackend : ICpuLivePowerBackend
{
    private readonly WindowsRz090528CpuNativeSession session;
    private readonly ICpuLivePowerTrustedNativeProvider provider;
    private readonly CpuNativeWorkerCoordinator state;

    internal CpuLivePowerNativeBackend(ICpuQueryWindowsPlatform platform, ICpuLivePowerTrustedNativeProvider provider)
        : this(WindowsRz090528CpuNativeSession.ForPlatform(platform), provider) { }
    internal CpuLivePowerNativeBackend(WindowsRz090528CpuNativeSession session, ICpuLivePowerTrustedNativeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(provider);
        this.session = session; this.provider = provider;
        state = CpuNativeWorkerCoordinator.ForSession(session);
    }
    internal Task? OutstandingWorker => state.OutstandingWorker;
    internal CpuLivePowerNativeBackendStatus Status
    { get { var status = state.Status; return new(status.WorkerOutstanding, status.Kind, status.Invalid); } }
    public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token) => ObserveAsync(token, null);
    public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic) => ObserveAsync(token, diagnostic, null);
    public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic,
        CpuLivePowerRecoveryTicket? recovery)
    {
        if (recovery is not null && !recovery.TryDispatch(this)) { return Task.FromResult(Empty()); }
        var timing = diagnostic?.BeginPowerObservation();
        return state.Start(CpuNativeWorkerFamily.Power, CpuNativeWorkerKind.Observation, workToken =>
        {
            try { return Observe(workToken, diagnostic, timing, recovery); }
            finally { diagnostic?.CompletePowerObservation(timing); }
        }, Empty(), token, diagnostic);
    }
    private CpuLivePowerBackendObservation Observe(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic,
        CpuPowerObservationTiming? timing, CpuLivePowerRecoveryTicket? recovery)
    {
        using var getter = provider.BeginGetter(token);
        var context = provider.ReadFreshContext(token);
        if (context is not { IsWellFormed: true }) { return Reject("power observation context"); }
        var installed = provider.CreateInstalledObservationApproval(context, token);
        var reader = provider.CreateObservationMetadata(context, token);
        if (installed is null || reader is null || !MatchesFresh(context, token)) { return Reject("power observation metadata/context"); }
        if (recovery is not null && !recovery.Matches(context, getter, installed, reader, token))
        { return Reject("power observation recovery anchor"); }
        CpuLivePowerNativeObservationResult observed;
        timing?.MoveTo(recovery is null ? CpuPowerObservationPhase.FirstNative : CpuPowerObservationPhase.ReplacementNative);
        observed = session.ObservePowerInstalled(installed, () => MatchesFresh(context, token)
            ? context.Identity : null, reader, token);
        // Do not renew this getter or reuse its poisoned wire. A ticket only
        // escapes after final admission, scope disposal, coordinator terminal
        // cleanup and the caller's existing bounded/runtime checks.
        if (recovery is null && getter is not null && observed.WorkerTerminal && observed.CleanupComplete && !observed.Batch.Complete
            && observed.Batch.Profile == CpuPmObservationProfile.RangeSingleSample
            && observed.Batch.FirmwareVersion == CpuPmObservationWireAdmission.ExpectedFirmwareVersion
            && observed.Batch.Diagnostic is
            {
                Stage: CpuPmObservationStage.Refresh, Failure: CpuPmObservationFailure.NativeFailure,
                NativeFailure: CpuQueryFailureCode.WireReplyRejected, Operation: 0x81112F24,
                WireRejection: CpuPmWireRejection.MailboxResponseNotSuccessful, MailboxStatus: 0xFD
            }
            && observed.Batch.CopySamples().Length == 0)
        {
            if (diagnostic is not null) { diagnostic.PowerEmptyWindowDetail = NativeDetail(observed.Batch.Diagnostic!); }
            token.ThrowIfCancellationRequested();
            timing?.MoveTo(CpuPowerObservationPhase.CompleteGetter);
            if (!provider.CompleteGetter(context, token)) { state.Invalidate(); return Reject("power observation final context"); }
            token.ThrowIfCancellationRequested();
            return Empty() with { RecoveryTicket = new(this, context, getter, installed, reader, token, NativeDetail(observed.Batch.Diagnostic!)) };
        }
        timing?.MoveTo(CpuPowerObservationPhase.PostRange);
        if (observed.Batch.Diagnostic is { } nativeDiagnostic)
        {
            diagnostic?.Check(false, NativeDetail(nativeDiagnostic));
        }
        static string NativeDetail(CpuPmObservationDiagnostic nativeDiagnostic) =>
            "power native observation " + nativeDiagnostic.Stage + "/" + nativeDiagnostic.Failure
                + (nativeDiagnostic.NativeFailure is { } failure ? "; native failure " + failure : string.Empty)
                + (nativeDiagnostic.NativeErrorCode is { } error ? "; native error " + error : string.Empty)
                + (nativeDiagnostic.Operation is { } operation ? $"; operation 0x{operation:X8}" : string.Empty)
                + (nativeDiagnostic.AdmissionFailure is { } admission ? "; admission " + admission : string.Empty)
                + (nativeDiagnostic.WireRejection is { } rejection ? "; wire rejection " + rejection : string.Empty)
                + (nativeDiagnostic.MailboxStatus is { } status ? $"; mailbox status 0x{status:X8}" : string.Empty);
        if (!observed.WorkerTerminal || !observed.CleanupComplete) { state.Invalidate(); return Reject("power observation cleanup"); }
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
            || CpuLivePowerCapability.TryDecode(rawCapability) is not { } capability) { return Reject("power observation batch shape"); }
        if (!MatchesFresh(context, token)) { state.Invalidate(); return Reject("power observation post-batch context"); }
        var range = provider.AdmitRequestedStateAndRange(context, batch, token);
        token.ThrowIfCancellationRequested();
        if (!MatchesFresh(context, token)) { state.Invalidate(); return Reject("power observation post-range context"); }
        if (range is not { Selector: 0x50001, StepMilliwatts: 1 } || range.MinimumMilliwatts > range.MaximumMilliwatts
            || range.MaximumMilliwatts > CpuLivePowerValue.MaximumMilliwatts) { return Reject("power observation trusted range"); }
        timing?.MoveTo(CpuPowerObservationPhase.CompleteGetter);
        if (!provider.CompleteGetter(context, token)) { state.Invalidate(); return Reject("power observation final context"); }
        return new(context, samples[0].Snapshot, cap, capability, range);

        CpuLivePowerBackendObservation Reject(string reason)
        {
            diagnostic?.Check(false, reason); return Empty();
        }
    }

    private bool MatchesFresh(CpuLivePowerContext? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return expected is { IsWellFormed: true } && provider.ReadFreshContext(token) is { IsWellFormed: true } current && expected.MatchesHardwareContext(current);
    }
    private static CpuLivePowerBackendObservation Empty() => new(null, null, null, null, null);
}
