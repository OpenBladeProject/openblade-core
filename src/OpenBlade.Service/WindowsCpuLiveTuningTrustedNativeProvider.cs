using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Optional installed integration: construction is inert. Only an initiating
// live request may retain the already-running, pinned driver. Never provisions it.
internal sealed class WindowsCpuLiveTuningTrustedNativeProvider : ICpuLiveCurveTrustedNativeProvider,
    ICpuLivePowerTrustedNativeProvider, IDisposable
{
    private readonly WindowsRz090528CpuNativeSession session;
    private readonly Func<CpuTuningController?> controller;
    private readonly ICpuTuningObservationSource source;
    private readonly Func<DeviceIdentity?> readUsb;
    private readonly Func<CancellationToken, CpuLiveCurveBootSequence?> readBoot;
    private readonly WindowsCpuLiveCurveRangeMetadata metadata;
    private readonly CpuNativeWorkerCoordinator worker;
    private CpuInstalledBackendLease? installed;
    private object? rangeRequest;
    private CpuLiveCurveContext? rangeContext;
    private CpuLiveCurveTrustedRange? range;
    private int disposed;
    private CpuLiveGetterAdmission? getterAdmission;
    private GetterScope? getterScope;
    private readonly Func<CpuGetterUsbContinuity?>? captureGetterUsb;
    private readonly Func<Func<bool>?>? captureGetterControllers;

    internal WindowsCpuLiveTuningTrustedNativeProvider(WindowsRz090528CpuNativeSession session,
        Func<CpuTuningController?> controller, ICpuTuningObservationSource source)
        : this(session, controller, source, Rz090528CpuFreshUsbIdentity.Read, new WindowsCpuBootSequenceReader().Read,
            new WindowsCpuLiveCurveRangeMetadata(), Rz090528CpuFreshUsbIdentity.CaptureGetter,
            Rz090528CpuControllerPresence.CaptureGetterContinuity)
    { }

    internal WindowsCpuLiveTuningTrustedNativeProvider(WindowsRz090528CpuNativeSession session,
        Func<CpuTuningController?> controller, ICpuTuningObservationSource source,
        Func<DeviceIdentity?> readUsb, Func<CancellationToken, CpuLiveCurveBootSequence?> readBoot,
        WindowsCpuLiveCurveRangeMetadata metadata, Func<CpuGetterUsbContinuity?>? captureGetterUsb = null,
        Func<Func<bool>?>? captureGetterControllers = null)
    {
        this.session = session; this.controller = controller; this.source = source;
        this.readUsb = readUsb; this.readBoot = readBoot; this.metadata = metadata;
        worker = CpuNativeWorkerCoordinator.ForSession(session);
        this.captureGetterUsb = captureGetterUsb; this.captureGetterControllers = captureGetterControllers;
    }

    public IDisposable? BeginGetter(CancellationToken token)
    {
        if (getterAdmission is not null) { throw new InvalidOperationException("A getter is already active."); }
        var admission = controller()?.BeginLiveGetter(token);
        if (admission is null) { return null; }
        getterAdmission = admission;
        return getterScope = new GetterScope(this, admission, token);
    }

    public CpuSavedAdmissionDiagnostic? Diagnostic => controller()?.CaptureLiveObservationDiagnostic();
    public bool SupportsReadPreparation => true;
    public CpuLiveCurveWriteProof? CreatePreparedWriteProof(CpuLiveCurveContext exactContext,
        CpuLiveCurveNativeRequest exactRequest, CancellationToken token)
    {
        if (getterAdmission is not null || !ValidCurveCommand(exactRequest.Command, exactRequest.Purpose)
            || exactRequest.Purpose == CpuLiveCurveCommandPurpose.Forward
                && !ValidCurveCommand(exactRequest.ImmediateReverse, CpuLiveCurveCommandPurpose.Restore)) { return null; }
        if (controller()?.CaptureLiveRuntimeAdmission(CpuTuningFeatures.CurveOptimizer, token)() != true) { return null; }
        var scope = BeginGetter(token);
        if (scope is null)
        {
            // Existing providers/fixtures without retained getter continuity keep
            // the full path; failure never falls back after a scope was created.
            if (!exactContext.MatchesHardwareContext(ReadFreshContext(token))
                || !CheckWriteAdmission(exactContext, exactRequest.Command, exactRequest.Purpose, token)
                || !exactContext.MatchesHardwareContext(ReadFreshContext(token))) { return null; }
            return CreateWriteProof(exactContext, exactRequest, token);
        }
        bool transferred = false;
        try
        {
            var getter = getterScope!;
            if (!exactContext.MatchesHardwareContext(ReadFreshContext(token))) { return null; }
            var current = Capture(exactContext, CpuTuningFeatures.CurveOptimizer, token, validateNow: false)!;
            var liveRequest = controller()?.CaptureLiveRuntimeRequest();
            if (!ReferenceEquals(liveRequest, rangeRequest) || rangeContext?.MatchesHardwareContext(exactContext) != true)
            {
                range = null; rangeRequest = liveRequest; rangeContext = exactContext;
                var result = ReadCurveMetadata(exactContext, current, token, getter);
                if (!result.CleanupComplete) { worker.Invalidate(); }
                if (result is { Status: CpuLiveCurveRangeStatus.Complete, CleanupComplete: true }) { range = result.Range; }
            }
            if (!getter.Current(exactContext) || range?.AllowsOperation(exactRequest) != true) { return null; }
            var proof = new WriteProof(exactRequest, exactContext, current, worker, this, token, getter);
            transferred = true;
            return proof;
        }
        finally { if (!transferred) { scope.Dispose(); } }
    }

    // A complete ordered preflight is read-only. Its result cannot be consumed
    // as a write proof; Send and the native setter retain independent admission.
    public bool CheckPlanAdmission(CpuLiveCurveContext exactContext,
        IReadOnlyList<CpuLiveCurveAdmissionCheck> checks, CancellationToken token)
    {
        if (checks.Count is < 1 or > 36 || getterAdmission is not null) { return false; }
        var admission = Capture(exactContext, CpuTuningFeatures.CurveOptimizer, token);
        if (admission is null) { return false; }
        using (var scope = BeginGetter(token))
        {
            var getter = getterScope;
            if (getter is null)
            {
                foreach (var check in checks)
                {
                    if (!CheckWriteAdmission(exactContext, check.Command, check.Purpose, token)) { return false; }
                }
            }
            else
            {
                if (!exactContext.MatchesHardwareContext(ReadFreshContext(token))) { return false; }
                var request = controller()?.CaptureLiveRuntimeRequest();
                if (!ReferenceEquals(request, rangeRequest) || rangeContext?.MatchesHardwareContext(exactContext) != true)
                {
                    range = null; rangeRequest = request; rangeContext = exactContext;
                    var result = ReadCurveMetadata(exactContext, admission, token, getter);
                    if (!result.CleanupComplete) { worker.Invalidate(); }
                    if (result is { Status: CpuLiveCurveRangeStatus.Complete, CleanupComplete: true }) { range = result.Range; }
                }
                if (range is not { } freshRange) { return false; }
                foreach (var check in checks)
                {
                    if (!getter.Current(exactContext) || !ValidCurveCommand(check.Command, check.Purpose)
                        || !freshRange.Allows(check.Command)) { return false; }
                }
                if (!CompleteGetter(exactContext, token)) { return false; }
            }
        }
        return !token.IsCancellationRequested && admission();
    }

    public CpuLiveCurveContext? ReadFreshContext(CancellationToken token)
    {
        var getter = getterScope;
        if (getter?.Context is { } context) { return getter.Current(context) ? context : null; }
        var current = ReadFreshContext(token, CaptureRuntimeAdmission(CpuTuningFeatures.None, token), getterAdmission);
        if (getter is not null && current is not null && !getter.Bind(current)) { return null; }
        return current;
    }

    private Func<bool>? CaptureRuntimeAdmission(CpuTuningFeatures feature, CancellationToken token)
    {
        var getter = getterAdmission;
        return feature == CpuTuningFeatures.None && getter is not null
            ? getter.Allows : controller()?.CaptureLiveRuntimeAdmission(feature, token);
    }

    private CpuLiveCurveContext? ReadFreshContext(CancellationToken token, Func<bool>? current, CpuLiveGetterAdmission? getter)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0) { getter?.Dispose(); return null; }
        var owner = controller();
        // Read only the initiating lease's diagnostic; no extra provider probe.
        var diagnostic = owner?.CaptureLiveObservationDiagnostic();
        using var stage = diagnostic?.BeginStage("native provider fresh context");
        bool Check(bool allowed, string stage)
        {
            if (!allowed) { getter?.Dispose(); }
            return diagnostic?.Check(allowed, stage) ?? allowed;
        }
        var lifetime = owner?.CaptureLiveRuntimeLifetime();
        if (!Check(current?.Invoke() == true, "provider runtime admission")
            || !Check(lifetime is { ServiceInstance: var instance } && instance != Guid.Empty, "provider runtime lifetime"))
        { return null; }
        var identity = source.ReadIdentity();
        var usb = readUsb();
        var boot = readBoot(token);
        if (!Check(CpuSavedTuningPlan.IsExactIdentity(identity), "provider exact host identity")
            || !Check(CpuHardwareIdentity.Same(identity, usb), "provider matching USB identity")
            || !Check(boot is { IsWellFormed: true }, "provider boot sequence")
            || !Check(current!(), "provider runtime after identity probes"))
        { return null; }
        // Capture the initiating admission in this callback as well. A late
        // retention attempt cannot borrow a later request's session permission.
        installed ??= session.TryRetainInstalledBackend(() => current!() ? readUsb() : null, token);
        if (!Check(installed?.IsCurrent == true, "provider retained installed backend")
            || !Check(current!(), "provider runtime after backend retention")) { return null; }
        return new(identity!, CpuLiveCurveSnapshot.SupportedMask, CpuLiveCurveSnapshot.SupportedPmVersion,
            CpuLiveCurveSnapshot.SupportedPmLength, PinnedIdentity(), boot!)
        { RuntimeLifetime = lifetime };
    }

    public CpuQueryInstalledApproval? CreateInstalledQueryApproval(CpuLiveCurveContext exactContext, CancellationToken token)
    {
        var getter = getterScope;
        Func<bool>? admission = getter is null ? Capture(exactContext, CpuTuningFeatures.None, token)
            : () => getter.Current(exactContext);
        return admission?.Invoke() != true ? null : installed?.CreateQueryApproval(admission);
    }

    CpuLivePowerContext? ICpuLivePowerTrustedNativeProvider.ReadFreshContext(CancellationToken token) => PowerContext(token);

    private CpuLivePowerContext? PowerContext(CancellationToken token)
    {
        var context = ReadFreshContext(token);
        return context is null ? null : new(context.Identity, context.EnabledCoreMask, context.PmVersion,
            context.PmTableLength, context.Backend, context.BootSequence)
        { RuntimeLifetime = context.RuntimeLifetime };
    }


    public CpuPmInstalledApproval? CreateInstalledObservationApproval(CpuLivePowerContext exactContext, CancellationToken token)
    {
        var admission = CapturePower(exactContext, token);
        return admission is null ? null : getterScope is not null
            ? installed?.CreateGetterPowerObservationApproval(admission)
            : installed?.CreatePowerObservationApproval(admission);
    }

    public IRz090528CpuLivePowerRangeMetadataReader? CreateObservationMetadata(CpuLivePowerContext exactContext, CancellationToken token)
    {
        var admission = CapturePower(exactContext, token);
        return admission is null ? null : new WindowsCpuLivePowerRangeMetadata(exactContext, new PowerRangeScope(admission), token);
    }

    public CpuLivePowerTrustedRange? AdmitRequestedStateAndRange(CpuLivePowerContext exactContext,
        CpuPmObservationBatch exactObservation, CancellationToken token)
    {
        var admission = CapturePower(exactContext, token);
        if (admission is null || !exactObservation.Complete
            || exactObservation.Profile != CpuPmObservationProfile.RangeSingleSample
            || exactObservation.FirmwareVersion != CpuPmObservationWireAdmission.ExpectedFirmwareVersion
            || exactObservation.RangeObservation is not { MetadataComplete: true, MinMax.Value: { Value: 0x50001, Stepping: 1 } limits }
            || limits.Min > limits.Max || limits.Max > CpuLivePowerValue.MaximumMilliwatts
            || exactObservation.CopySamples() is not [var sample] || CpuLivePowerSnapshot.TryCreate(sample.Snapshot) is null)
        { return null; }
        // The reviewed exact firmware publishes accepted targets at +08/+10.
        // Current in WMI metadata is a shadow and never supplies this baseline.
        return admission() ? new(0x50001, limits.Min, limits.Max, 1) : null;
    }

    private Func<bool>? CapturePower(CpuLivePowerContext context, CancellationToken token)
    {
        var current = PowerContext(token);
        if (!context.MatchesHardwareContext(current)) { return null; }
        var curve = new CpuLiveCurveContext(context.Identity, context.EnabledCoreMask, context.PmVersion,
            context.PmTableLength, context.Backend, context.BootSequence)
        { RuntimeLifetime = context.RuntimeLifetime };
        return getterScope is { } getter ? () => getter.Current(curve) : Capture(curve, CpuTuningFeatures.None, token);
    }

    public bool CheckWriteAdmission(CpuLiveCurveContext exactContext, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token)
    {
        if (getterAdmission is not null) { return false; }
        if (!ValidCurveCommand(command, purpose)) { return false; }
        var admission = Capture(exactContext, CpuTuningFeatures.CurveOptimizer, token);
        if (admission is null) { return false; }
        var request = controller()?.CaptureLiveRuntimeRequest();
        if (!ReferenceEquals(request, rangeRequest) || rangeContext?.MatchesHardwareContext(exactContext) != true)
        {
            range = null; rangeRequest = request; rangeContext = exactContext;
            var result = ReadCurveMetadata(exactContext, admission, token);
            if (!result.CleanupComplete) { worker.Invalidate(); }
            if (result.Status == CpuLiveCurveRangeStatus.Complete && result.CleanupComplete && admission())
            { range = result.Range; }
        }
        return range?.Allows(command) == true && admission();
    }

    private static bool ValidCurveCommand(CpuLiveCurveCommand command, CpuLiveCurveCommandPurpose purpose)
    {
        if (!Enum.IsDefined(purpose) || purpose == CpuLiveCurveCommandPurpose.Forward && command.Offset is < -30 or > 0
            || !CpuLiveCurveCommand.TryDecode(CpuLiveCurveCommand.Message, command.EncodeArgument(),
                CpuLiveCurveSnapshot.SupportedMask, out var decoded) || decoded != command) { return false; }
        return true;
    }

    public CpuLiveCurveWriteProof? CreateWriteProof(CpuLiveCurveContext exactContext,
        CpuLiveCurveNativeRequest exactRequest, CancellationToken token)
    {
        if (getterAdmission is not null) { return null; }
        var admission = Capture(exactContext, CpuTuningFeatures.CurveOptimizer, token);
        if (admission is null || !CheckWriteAdmission(exactContext, exactRequest.Command, exactRequest.Purpose, token)
            || exactRequest.Purpose == CpuLiveCurveCommandPurpose.Forward
                && !CheckWriteAdmission(exactContext, exactRequest.ImmediateReverse, CpuLiveCurveCommandPurpose.Restore, token)
            || !admission()) { return null; }
        return new WriteProof(exactRequest, exactContext, admission, worker, this, token);
    }

    private Func<bool>? Capture(CpuLiveCurveContext context, CpuTuningFeatures feature, CancellationToken token, bool validateNow = true)
    {
        var owner = controller();
        var getter = feature == CpuTuningFeatures.None ? getterAdmission : null;
        var admission = CaptureRuntimeAdmission(feature, token);
        // Preserve the nested read-context feature checks for writes; only this
        // getter's callback shares its power proof with the nested read checks.
        var contextAdmission = getter is null
            ? owner?.CaptureLiveRuntimeAdmission(CpuTuningFeatures.None, token) : admission;
        var diagnostic = owner?.CaptureLiveObservationDiagnostic();
        bool Check(bool allowed, string stage) => diagnostic?.Check(allowed, stage) ?? allowed;
        int revoked = 0;
        bool Current()
        {
            if (Volatile.Read(ref revoked) != 0) { return false; }
            bool accepted;
            try
            {
                accepted = Check(!token.IsCancellationRequested, "provider query approval cancellation")
                    && Check(admission?.Invoke() == true, "provider query approval runtime admission")
                    && Check(installed?.IsCurrent == true, "provider query approval installed backend")
                    && Check(context.RuntimeLifetime is not null, "provider query approval runtime lifetime")
                    && Check(context.MatchesHardwareContext(ReadFreshContext(token, contextAdmission, getter)), "provider query approval hardware context")
                    && Check(admission!(), "provider query approval final runtime admission");
            }
            catch { accepted = false; }
            if (!accepted) { getter?.Dispose(); Interlocked.Exchange(ref revoked, 1); }
            return accepted && Volatile.Read(ref revoked) == 0;
        }
        return !validateNow || Current() ? Current : null;
    }

    private static CpuLiveCurveBackendIdentity PinnedIdentity() => new(
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DriverPath],
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Platform.dll"],
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Device.dll"]);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        getterAdmission?.Dispose();
        installed?.Dispose();
    }

    bool ICpuLivePowerTrustedNativeProvider.CompleteGetter(CpuLivePowerContext expected, CancellationToken token) =>
        CompleteGetter(new CpuLiveCurveContext(expected.Identity, expected.EnabledCoreMask, expected.PmVersion,
            expected.PmTableLength, expected.Backend, expected.BootSequence)
        { RuntimeLifetime = expected.RuntimeLifetime }, token);

    public bool CompleteGetter(CpuLiveCurveContext expected, CancellationToken token)
    {
        var getter = getterScope;
        if (getter is null) { return expected.MatchesHardwareContext(ReadFreshContext(token)); }
        if (!getter.Current(expected) || !getter.Admission.Revalidate()) { return getter.Reject(); }
        var fresh = ReadFreshContext(token, getter.Admission.Allows, getter.Admission);
        return expected.MatchesHardwareContext(fresh) && getter.Current(expected) || getter.Reject();
    }

    private sealed class GetterScope(WindowsCpuLiveTuningTrustedNativeProvider owner,
        CpuLiveGetterAdmission admission, CancellationToken token) : IDisposable
    {
        internal CpuLiveGetterAdmission Admission => admission;
        internal CpuLiveCurveContext? Context { get; private set; }
        private CpuGetterUsbContinuity? usb;
        private Func<bool>? controllers;
        private bool disposed;
        internal bool IsDisposed => disposed;
        internal bool Bind(CpuLiveCurveContext context)
        {
            Context = context;
            usb = owner.captureGetterUsb?.Invoke();
            controllers = owner.captureGetterControllers?.Invoke();
            if (owner.captureGetterUsb is not null && usb is null
                || owner.captureGetterControllers is not null && controllers is null) { return Reject(); }
            return Current(context);
        }
        internal bool Current(CpuLiveCurveContext expected)
        {
            var diagnostic = owner.Diagnostic;
            bool Check(Func<bool> predicate, string name)
            {
                using var stage = diagnostic?.BeginStage(name);
                try
                {
                    bool allowed = predicate();
                    return diagnostic?.Check(allowed, name) ?? allowed;
                }
                catch { diagnostic?.Check(false, name + " exception"); return false; }
            }
            try
            {
                return Check(() => !disposed, "provider getter scope active")
                    && Check(() => !token.IsCancellationRequested, "provider getter cancellation")
                    && Check(admission.Allows, "provider getter admission before continuity")
                    && Check(() => Context is { } value && expected.MatchesHardwareContext(value), "provider getter matching context")
                    && Check(() => owner.installed?.GetterContinuityCurrent == true, "provider getter installed backend continuity")
                    && Check(() => usb is not null ? usb.IsCurrent() : CpuHardwareIdentity.Same(Context!.Identity, owner.readUsb()), "provider getter USB continuity")
                    && Check(() => controllers?.Invoke() ?? owner.captureGetterControllers is null, "provider getter controller continuity")
                    && Check(() => Context!.BootSequence == owner.readBoot(token), "provider getter boot continuity")
                    && Check(() => owner.controller()?.CaptureLiveRuntimeLifetime() == Context!.RuntimeLifetime, "provider getter runtime lifetime")
                    && Check(() => Volatile.Read(ref owner.disposed) == 0, "provider getter owner active")
                    && Check(admission.Allows, "provider getter admission after continuity")
                    || Reject();
            }
            catch { return Reject(); }
        }
        internal bool Reject() { admission.Dispose(); return false; }
        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true; admission.Dispose();
            try { usb?.Dispose(); }
            catch { owner.worker.Invalidate(); throw; }
            finally
            {
                if (ReferenceEquals(owner.getterScope, this))
                { owner.getterAdmission = null; owner.getterScope = null; }
            }
        }
    }

    private CpuLiveCurveRangeResult ReadCurveMetadata(CpuLiveCurveContext expected, Func<bool> fullAdmission,
        CancellationToken token, GetterScope? borrowed = null)
    {
        if (borrowed is not null)
        {
            // Only the exact write proof can pass its own active read scope.
            // The enclosing scope owns final revalidation and disposal.
            if (!ReferenceEquals(borrowed, getterScope) || !borrowed.Current(expected))
            { return new(null, CpuLiveCurveRangeStatus.AdmissionDenied, true, new(), new()); }
            var borrowedResult = metadata.Read(expected, new RangeScope(() => borrowed.Current(expected)), token);
            return borrowed.Current(expected) ? borrowedResult
                : borrowedResult with { Range = null, Status = CpuLiveCurveRangeStatus.AdmissionDenied };
        }
        using var scope = BeginGetter(token);
        var getter = getterScope;
        if (getter is null) { return metadata.Read(expected, new RangeScope(fullAdmission), token); }
        if (!expected.MatchesHardwareContext(ReadFreshContext(token)))
        { getter.Reject(); return new(null, CpuLiveCurveRangeStatus.AdmissionDenied, true, new(), new()); }
        var result = metadata.Read(expected, new RangeScope(() => getter.Current(expected)), token);
        if (result.Status != CpuLiveCurveRangeStatus.Complete || !result.CleanupComplete) { return result; }
        return CompleteGetter(expected, token) ? result
            : result with { Range = null, Status = CpuLiveCurveRangeStatus.AdmissionDenied };
    }

    private sealed class RangeScope(Func<bool> current) : CpuLiveCurveRangeTrustedScope
    {
        internal override bool IsCurrent(CpuLiveCurveContext exactContext) => current();
    }

    private sealed class PowerRangeScope(Func<bool> current) : CpuLivePowerRangeTrustedScope
    {
        internal override bool IsCurrent(CpuLivePowerContext exactContext) => current();
    }

    private sealed class WriteProof(CpuLiveCurveNativeRequest request, CpuLiveCurveContext context,
        Func<bool> current, CpuNativeWorkerCoordinator worker,
        WindowsCpuLiveTuningTrustedNativeProvider owner, CancellationToken token,
        GetterScope? preparation = null) : CpuLiveCurveWriteProof(request)
    {
        private bool read;
        private CpuLiveCurveTrustedRange? range;
        private readonly GetterScope? initialPreparation = preparation;
        private GetterScope? readScope = preparation;
        private bool preparationAdopted;
        private int readScopesStarted;
        private GetterScope? ActiveRead => readScope is { IsDisposed: false } ? readScope : null;
        private bool CheckRead(CpuLiveCurveNativeRequest exactRequest) => ActiveRead is null
            ? Current(exactRequest) : ReadCurrent(exactRequest);
        internal override bool IsPreparationCurrent(CpuLiveCurveNativeRequest exactRequest)
        {
            if (initialPreparation is null) { return IsCurrent(exactRequest); }
            using var stage = owner.Diagnostic?.BeginStage("curve native retained read preparation");
            return !preparationAdopted && ReferenceEquals(owner.getterScope, initialPreparation) && initialPreparation.Current(context);
        }
        internal override IDisposable? BeginReadCore(CpuLiveCurveNativeRequest exactRequest)
        {
            if (initialPreparation is not null && !preparationAdopted)
            {
                if (!IsPreparationCurrent(exactRequest)) { throw new InvalidOperationException("CPU read preparation was revoked."); }
                preparationAdopted = true; readScopesStarted++;
                return initialPreparation;
            }
            if (ActiveRead is not null) { throw new InvalidOperationException("CPU operation read scope is already active."); }
            var scope = owner.BeginGetter(token);
            if (scope is null) { return null; } // Existing full checks remain the fallback.
            try
            {
                readScope = owner.getterScope;
                if (!context.MatchesHardwareContext(owner.ReadFreshContext(token)))
                { throw new InvalidOperationException("CPU operation read context changed."); }
                readScopesStarted++;
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }
        internal override bool IsReadCurrent(CpuLiveCurveNativeRequest exactRequest) => ActiveRead is { } getter
            ? ReferenceEquals(owner.getterScope, getter) && getter.Current(context) : current();
        internal override bool CompleteReadCore(CpuLiveCurveNativeRequest exactRequest) => ActiveRead is { } getter
            ? ReferenceEquals(owner.getterScope, getter) && owner.CompleteGetter(context, token) : current();
        internal override bool AdmitPowerPeer(CpuLiveCurveNativeRequest exactRequest, uint capability, CpuLivePowerFusedCap cap)
        {
            if (!CheckRead(exactRequest) || exactRequest.PowerPeer is not { } peer
                || CpuLivePowerCapability.TryDecode(capability) is null) { return false; }
            // Historical PPT ownership is never silently promoted to read-only authority.
            if (peer.Original is not null) { return false; }
            return CheckRead(exactRequest);
        }
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest)
        {
            // Preparation can be consumed for reads, never as setter authority.
            if (ActiveRead is not null) { return false; }
            using var stage = owner.Diagnostic?.BeginStage(readScopesStarted switch
            {
                0 => "curve native initial full proof",
                1 => "curve setter barrier full proof",
                _ => "curve final readback full proof",
            });
            return current();
        }
        internal override void DisposeCore() => readScope?.Dispose();
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
        {
            if (!CheckRead(exactRequest)) { return null; }
            // One fresh, completely decoded getter pair under this exact native
            // lease. Repeated checks do not invoke the same WMI getter again.
            if (!read)
            {
                read = true;
                var result = owner.ReadCurveMetadata(context, current, token, ActiveRead);
                if (!result.CleanupComplete) { worker.Invalidate(); }
                if (result is { Status: CpuLiveCurveRangeStatus.Complete, CleanupComplete: true }) { range = result.Range; }
            }
            return CheckRead(exactRequest) && range?.AllowsOperation(exactRequest) == true ? range : null;
        }
    }
}
