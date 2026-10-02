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
        var current = owner?.CaptureLiveRuntimeAdmission(CpuTuningFeatures.None);
        var lifetime = owner?.CaptureLiveRuntimeLifetime();
        if (current?.Invoke() != true || lifetime is not { ServiceInstance: var instance } || instance == Guid.Empty)
        { return null; }
        var identity = source.ReadIdentity();
        var usb = readUsb();
        var boot = readBoot(token);
        if (!CpuSavedTuningPlan.IsExactIdentity(identity) || !CpuHardwareIdentity.Same(identity, usb)
            || boot is not { IsWellFormed: true } || !current())
        { return null; }
        // Capture the initiating admission in this callback as well. A late
        // retention attempt cannot borrow a later request's session permission.
        installed ??= session.TryRetainInstalledBackend(() => current() ? readUsb() : null, token);
        if (installed?.IsCurrent != true || !current()) { return null; }
        return new(identity!, CpuLiveCurveSnapshot.SupportedMask, CpuLiveCurveSnapshot.SupportedPmVersion,
            CpuLiveCurveSnapshot.SupportedPmLength, PinnedIdentity(), boot)
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
        var admission = Capture(exactContext, CpuTuningFeatures.CurveOptimizer, token);
        if (admission is null || !CheckWriteAdmission(exactContext, exactRequest.Command, exactRequest.Purpose, token)
            || exactRequest.Purpose == CpuLiveCurveCommandPurpose.Forward
                && !CheckWriteAdmission(exactContext, exactRequest.ImmediateReverse, CpuLiveCurveCommandPurpose.Restore, token)
            || !admission()) { return null; }
        return new WriteProof(exactRequest, exactContext, admission, metadata, worker, token);
    }

    private Func<bool>? Capture(CpuLiveCurveContext context, CpuTuningFeatures feature, CancellationToken token)
    {
        var admission = controller()?.CaptureLiveRuntimeAdmission(feature);
        int revoked = 0;
        bool Current()
        {
            if (Volatile.Read(ref revoked) != 0) { return false; }
            bool accepted;
            try
            {
                accepted = !token.IsCancellationRequested && admission?.Invoke() == true
                    && installed?.IsCurrent == true && context.RuntimeLifetime is not null
                    && context.MatchesHardwareContext(ReadFreshContext(token)) && admission();
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
        CancellationToken token) : CpuLiveCurveWriteProof(request)
    {
        private bool read;
        private CpuLiveCurveTrustedRange? range;
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
