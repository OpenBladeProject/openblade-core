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

    internal WindowsCpuLiveTuningTrustedNativeProvider(WindowsRz090528CpuNativeSession session,
        Func<CpuTuningController?> controller, ICpuTuningObservationSource source)
        : this(session, controller, source, Rz090528CpuFreshUsbIdentity.Read, new WindowsCpuBootSequenceReader().Read,
            new WindowsCpuLiveCurveRangeMetadata())
    { }

    internal WindowsCpuLiveTuningTrustedNativeProvider(WindowsRz090528CpuNativeSession session,
        Func<CpuTuningController?> controller, ICpuTuningObservationSource source,
        Func<DeviceIdentity?> readUsb, Func<CancellationToken, CpuLiveCurveBootSequence?> readBoot,
        WindowsCpuLiveCurveRangeMetadata metadata)
    {
        this.session = session; this.controller = controller; this.source = source;
        this.readUsb = readUsb; this.readBoot = readBoot; this.metadata = metadata;
        worker = CpuNativeWorkerCoordinator.ForSession(session);
    }

    public CpuLiveCurveContext? ReadFreshContext(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0) { return null; }
        var owner = controller();
        // Read only the initiating lease's diagnostic; no extra provider probe.
        var diagnostic = owner?.CaptureLiveObservationDiagnostic();
        using var stage = diagnostic?.BeginStage("native provider fresh context");
        bool Check(bool allowed, string stage) => diagnostic?.Check(allowed, stage) ?? allowed;
        var current = owner?.CaptureLiveRuntimeAdmission(CpuTuningFeatures.None, token);
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

    // Attended developer approval is never manufactured by the product.
    public CpuQueryAttendedApproval? CreateQueryApproval(CpuLiveCurveContext exactContext, CancellationToken token) => null;

    public CpuQueryInstalledApproval? CreateInstalledQueryApproval(CpuLiveCurveContext exactContext, CancellationToken token)
    {
        var admission = Capture(exactContext, CpuTuningFeatures.None, token);
        return admission is null ? null : installed?.CreateQueryApproval(admission);
    }

    CpuLivePowerContext? ICpuLivePowerTrustedNativeProvider.ReadFreshContext(CancellationToken token) => PowerContext(token);

    private CpuLivePowerContext? PowerContext(CancellationToken token)
    {
        var context = ReadFreshContext(token);
        return context is null ? null : new(context.Identity, context.EnabledCoreMask, context.PmVersion,
            context.PmTableLength, context.Backend, context.BootSequence)
        { RuntimeLifetime = context.RuntimeLifetime };
    }

    public CpuPmAttendedApproval? CreateObservationApproval(CpuLivePowerContext exactContext, CancellationToken token) => null;
    public CpuPmRunOwnedDriverLifetime? ReadFreshObservationLifetime(CpuLivePowerContext exactContext, CancellationToken token) => null;

    public CpuPmInstalledApproval? CreateInstalledObservationApproval(CpuLivePowerContext exactContext, CancellationToken token)
    {
        var admission = CapturePower(exactContext, token);
        return admission is null ? null : installed?.CreatePowerObservationApproval(admission);
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

    // Native PPT production writes await the reviewed physical setter fixture.
    public bool CheckWriteAdmission(CpuLivePowerContext exactContext, CpuLivePowerCommand command,
        CpuLivePowerCommandPurpose purpose, CpuLivePowerPair expectedBefore, CpuLivePowerPair original, CancellationToken token) => false;
    public CpuLivePowerWriteProof? CreateWriteProof(CpuLivePowerContext exactContext,
        CpuLivePowerNativeRequest exactRequest, CancellationToken token) => null;

    private Func<bool>? CapturePower(CpuLivePowerContext context, CancellationToken token)
    {
        var current = PowerContext(token);
        if (!context.MatchesHardwareContext(current)) { return null; }
        var curve = new CpuLiveCurveContext(context.Identity, context.EnabledCoreMask, context.PmVersion,
            context.PmTableLength, context.Backend, context.BootSequence)
        { RuntimeLifetime = context.RuntimeLifetime };
        return Capture(curve, CpuTuningFeatures.None, token);
    }

    public bool CheckWriteAdmission(CpuLiveCurveContext exactContext, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token)
    {
#if OPENBLADE_CPU_VOLTAGE_VALIDATION
        // The developer profile narrows every purpose, including first-original
        // restoration. Fresh firmware metadata remains an independent intersection.
        if (command.Offset is not (-1 or 0)) { return false; }
#endif
        if (!Enum.IsDefined(purpose) || purpose == CpuLiveCurveCommandPurpose.Forward && command.Offset is < -30 or > 0
            || !CpuLiveCurveCommand.TryDecode(CpuLiveCurveCommand.Message, command.EncodeArgument(),
                CpuLiveCurveSnapshot.SupportedMask, out var decoded) || decoded != command) { return false; }
        var admission = Capture(exactContext, CpuTuningFeatures.CurveOptimizer, token);
        if (admission is null) { return false; }
        var request = controller()?.CaptureLiveRuntimeRequest();
        if (!ReferenceEquals(request, rangeRequest) || rangeContext?.MatchesHardwareContext(exactContext) != true)
        {
            range = null; rangeRequest = request; rangeContext = exactContext;
            var result = metadata.Read(exactContext, new RangeScope(admission), token);
            if (!result.CleanupComplete) { worker.Invalidate(); }
            if (result.Status == CpuLiveCurveRangeStatus.Complete && result.CleanupComplete && admission())
            { range = result.Range; }
        }
        return range?.Allows(command) == true && admission();
    }

    public CpuLiveCurveWriteProof? CreateWriteProof(CpuLiveCurveContext exactContext,
        CpuLiveCurveNativeRequest exactRequest, CancellationToken token)
    {
#if OPENBLADE_CPU_VOLTAGE_VALIDATION
        if (exactRequest.Before.CoreOffsets.Any(core => core.Offset is not (-1 or 0))) { return null; }
#endif
        var admission = Capture(exactContext, CpuTuningFeatures.CurveOptimizer, token);
        if (admission is null || !CheckWriteAdmission(exactContext, exactRequest.Command, exactRequest.Purpose, token)
            || exactRequest.Purpose == CpuLiveCurveCommandPurpose.Forward
                && !CheckWriteAdmission(exactContext, exactRequest.ImmediateReverse, CpuLiveCurveCommandPurpose.Restore, token)
            || !admission()) { return null; }
        return new WriteProof(exactRequest, exactContext, admission, metadata, worker, this, token);
    }

    private Func<bool>? Capture(CpuLiveCurveContext context, CpuTuningFeatures feature, CancellationToken token)
    {
        var owner = controller();
        var admission = owner?.CaptureLiveRuntimeAdmission(feature, token);
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
                    && Check(context.MatchesHardwareContext(ReadFreshContext(token)), "provider query approval hardware context")
                    && Check(admission!(), "provider query approval final runtime admission");
            }
            catch { accepted = false; }
            if (!accepted) { Interlocked.Exchange(ref revoked, 1); }
            return accepted && Volatile.Read(ref revoked) == 0;
        }
        return Current() ? Current : null;
    }

    private static CpuLiveCurveBackendIdentity PinnedIdentity() => new(
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DriverPath],
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Platform.dll"],
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Device.dll"]);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        installed?.Dispose();
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
        Func<bool> current, WindowsCpuLiveCurveRangeMetadata metadata, CpuNativeWorkerCoordinator worker,
        WindowsCpuLiveTuningTrustedNativeProvider owner, CancellationToken token) : CpuLiveCurveWriteProof(request)
    {
        private bool read;
        private CpuLiveCurveTrustedRange? range;
        internal override bool AdmitPowerPeer(CpuLiveCurveNativeRequest exactRequest, uint capability, CpuLivePowerFusedCap cap)
        {
            if (!Current(exactRequest) || exactRequest.PowerPeer is not { } peer
                || CpuLivePowerCapability.TryDecode(capability) is null) { return false; }
            // The unchanged, unowned peer requires observation authority only.
            // Any owned original still needs independent PPT restoration admission;
            // the production PPT gate deliberately remains closed pending fixture.
            if (peer.Original is { } original)
            {
                var peerContext = new CpuLivePowerContext(context.Identity, context.EnabledCoreMask, context.PmVersion,
                    context.PmTableLength, context.Backend, context.BootSequence)
                { RuntimeLifetime = context.RuntimeLifetime };
                foreach (var limit in new[] { CpuLivePowerLimit.Fast, CpuLivePowerLimit.Slow })
                {
                    if (peer.Expected.Get(limit) == original.Get(limit)) { continue; }
                    if (!CpuLivePowerCommand.TryCreate(limit, original.Get(limit).Milliwatts, out var restore)
                        || !owner.CheckWriteAdmission(peerContext, restore, CpuLivePowerCommandPurpose.Restore,
                            peer.Expected, original, token)) { return false; }
                }
            }
            return current();
        }
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) => current();
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
        {
            if (!current()) { return null; }
            // One fresh, completely decoded getter pair under this exact native
            // lease. Repeated checks do not invoke the same WMI getter again.
            if (!read)
            {
                read = true;
                var result = metadata.Read(context, new RangeScope(current), token);
                if (!result.CleanupComplete) { worker.Invalidate(); }
                if (result is { Status: CpuLiveCurveRangeStatus.Complete, CleanupComplete: true }) { range = result.Range; }
            }
            return current() && range?.AllowsOperation(exactRequest) == true ? range : null;
        }
    }
}
