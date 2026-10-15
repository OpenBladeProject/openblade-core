using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Exact-model, upstream-derived experimental controls.</summary>
internal sealed class ExperimentalBladeFactory : IProductionBatteryPortFactory
{
    private readonly ExperimentalBladeModel model;
    private readonly ExperimentalFeaturesPolicy policy;
    private readonly IHostIdentitySource identity;
    private readonly IHidCandidateNative native;
    private readonly IHardwareAccessGuard guard;
    private readonly IHidFeatureIo io;
    private readonly Func<PowerSource> capturePowerSource;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ExperimentalLightingOwnershipState lightingOwnership = new();

    internal ExperimentalBladeFactory(ExperimentalBladeModel model, IHostIdentitySource identity,
        IHidCandidateNative native, IHardwareAccessGuard guard, IHidFeatureIo io,
        Func<PowerSource> capturePowerSource)
    {
        this.model = model;
        this.identity = identity;
        this.native = native;
        this.guard = guard;
        this.io = io;
        this.capturePowerSource = capturePowerSource;
        policy = new(model);
    }

    public string EvidenceSource => model.EvidenceSource;

    public DeviceCapabilities Capabilities => ExperimentalBladePort.CreateCapabilities(model, policy.WritesAllowed);

    public DeviceCapabilities PotentialCapabilities => ExperimentalBladePort.CreateCapabilities(model, true);

    public IProductionLightingPortFactory CreateLightingFactory() => new ExperimentalLightingFactory(this);

    private sealed class ExperimentalLightingFactory(ExperimentalBladeFactory owner) : IProductionLightingPortFactory
    {
        public DeviceCapabilities Capabilities => owner.Capabilities;

        public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(CancellationToken token) =>
            OpenAsync(() => true, token);

        public async Task<HidSessionResult<IProductionLightingPort>> OpenAsync(Func<bool> writeAllowed, CancellationToken token)
        {
            if (!ExperimentalBladePort.SupportsExperimentalLighting(owner.model))
            {
                return new(null, HidSessionFailureKind.IdentityBlocked);
            }
            HidSessionResult<IProductionBatteryPort> opened = await owner.OpenAsync(token).ConfigureAwait(false);
            if (opened.Value is not ExperimentalBladePort port || opened.Failure is not null)
            {
                if (opened.Value is not null)
                {
                    await opened.Value.DisposeAsync().ConfigureAwait(false);
                }
                return new(null, opened.Failure ?? HidSessionFailureKind.IdentityBlocked) { Diagnostic = opened.Diagnostic };
            }
            port.SetAdditionalWriteAdmission(writeAllowed);
            return new(port, null);
        }
    }

    public bool IsWritesPaused => policy.IsPaused;

    public async Task<ConfirmedDeviceState> ResumeWritesAsync(
        Func<ConfirmedDeviceState, bool> acceptObservedState,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptObservedState);
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        cancellationToken.ThrowIfCancellationRequested();
        if (!policy.IsPaused || !stillAuthorized())
        {
            throw new CapabilityUnavailableException("Experimental writes are not paused or recovery is no longer authorized.");
        }
        policy.Record("WriteRecovery", "Recovery", "Started");
        try
        {
            // Always open a new session: the session that failed may be poisoned.
            HidSessionResult<IProductionBatteryPort> opened = await OpenAsync(cancellationToken).ConfigureAwait(false);
            if (opened.Value is not ExperimentalBladePort port)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException($"Experimental recovery admission failed: {opened.Failure}.");
            }
            ConfirmedDeviceState observed;
            await using (port.ConfigureAwait(false))
            {
                if (opened.Failure is not null)
                {
                    throw new IOException($"Experimental recovery admission failed: {opened.Failure}.");
                }
                observed = await port.ReadRecoveryStateAsync(cancellationToken).ConfigureAwait(false);
                if (!port.IsRecoverySessionHealthy)
                {
                    throw new IOException("Experimental recovery session did not remain healthy.");
                }
            }
            // Finish all native work and cleanup before committing runtime reconciliation.
            cancellationToken.ThrowIfCancellationRequested();
            HardwareAccessDecision access = guard.Check();
            if (!port.IsRecoverySessionDrained
                || identity.ReadExactIdentity() is not { Evidence: not null, Failure: null }
                || access.DetectionFailure is not null || access.Conflicts.Count != 0 || !stillAuthorized())
            {
                throw new CapabilityUnavailableException("Fresh recovery safety admission failed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!acceptObservedState(observed))
            {
                throw new CapabilityUnavailableException("The confirmed state could not be reconciled for recovery.");
            }
            // Acceptance synchronously commits reconciliation under the runtime gate.
            // No asynchronous work or fallible admission may split that commit.
            policy.CompleteRecovery();
            policy.Record("WriteRecovery", "Recovery", "Resumed");
            return observed;
        }
        catch (Exception exception)
        {
            policy.Record("WriteRecovery", "Recovery", "Failed", failure: exception.GetType().Name);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    public DiagnosticExperimentalSupport GetDiagnostics() => policy.Snapshot();

    public static ExperimentalBladeFactory? CreateForCurrentHost(Func<PowerSource> capturePowerSource)
    {
        ArgumentNullException.ThrowIfNull(capturePowerSource);
        ExperimentalBladeModel? model = ExperimentalBladeModels.FindCurrentHost();
        if (model is null)
        {
            return null;
        }
        var registry = new WindowsRegistryValueSource();
        var native = new WindowsHidNative();
        return new(model,
            ExperimentalBladeModels.CreateIdentity(model, registry,
                new RuntimeWindowsPlatformEvidenceSource(registry)), native,
            new WindowsHardwareAccessGuard(new RunningProcessSource(), new WindowsServiceSource()),
            new OverlappedFeatureOperation(native), capturePowerSource);
    }

    public BatteryValidationProbeResult Probe()
    {
        HostIdentityReadResult host = identity.ReadExactIdentity();
        var result = new BatteryValidationProbeResult(
            BatteryValidationProbeStage.HostIdentity, BatteryValidationProbeClassification.HostIdentityBlocked,
            false, false, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0,
            BatteryValidationFirmwareCompatibility.ModelScoped, host.HostIdentityFailure,
            host.Evidence?.Sku);
        if (host.Evidence is null || host.Failure is not null)
        {
            return result;
        }
        try
        {
            HardwareAccessDecision access = guard.Check();
            if (access.DetectionFailure is not null || access.Conflicts.Count != 0)
            {
                return result with
                {
                    HostIdentityMatched = true,
                    Stage = BatteryValidationProbeStage.ConflictCheck,
                    Classification = access.DetectionFailure is not null
                        ? BatteryValidationProbeClassification.ConflictDetectionFailed
                        : BatteryValidationProbeClassification.ConflictBlocked,
                };
            }
            List<(OpenedCandidateHandle Handle, HidHandleIdentity Identity)> candidates = FindCandidates();
            int count = candidates.Count;
            foreach (var candidate in candidates)
            {
                candidate.Handle.Dispose();
            }
            return result with
            {
                HostIdentityMatched = true,
                ConflictCheckPassed = true,
                Stage = count == 0 ? BatteryValidationProbeStage.Cardinality : BatteryValidationProbeStage.Ready,
                Classification = count == 0 ? BatteryValidationProbeClassification.NoExactHandle
                    : BatteryValidationProbeClassification.Ready,
                // Interface usages are observed metadata; functional GET admission occurs in OpenAsync.
                ProductMatchCount = count,
                VendorMatchCount = count,
                FeatureLengthMatchCount = count,
                ExactMatchCount = count,
            };
        }
        catch
        {
            return result with
            {
                HostIdentityMatched = true,
                Stage = BatteryValidationProbeStage.Enumeration,
                Classification = BatteryValidationProbeClassification.EnumerationFailed,
            };
        }
    }

    public async Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Admission uses GET commands only. A canceled probe cannot have applied a control write.
            return new(null, HidSessionFailureKind.Canceled);
        }
    }

    private async Task<HidSessionResult<IProductionBatteryPort>> OpenCoreAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(null, HidSessionFailureKind.Canceled);
        }
        if (identity.ReadExactIdentity() is not { Evidence: not null, Failure: null })
        {
            return new(null, HidSessionFailureKind.IdentityBlocked);
        }
        HardwareAccessDecision access = guard.Check();
        if (access.DetectionFailure is not null || access.Conflicts.Count != 0)
        {
            return new(null, access.DetectionFailure is not null
                ? HidSessionFailureKind.ConflictDetectionFailed : HidSessionFailureKind.ConflictBlocked);
        }

        List<(OpenedCandidateHandle Handle, HidHandleIdentity Identity)> candidates = FindCandidates();
        ExperimentalBladePort? selected = null;
        bool ambiguous = false;
        ExperimentalControlObservation? lastAdmissionFailure = null;
        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Pin the complete descriptor read from this exact handle. Never borrow another model's usage.
                if (native.ReadIdentity(candidate.Handle) != candidate.Identity)
                {
                    continue;
                }
                var context = new ValidationRunContext();
                if (!context.TryAcquireActor(out ValidationRunContext.ActorLease lease))
                {
                    return new(null, HidSessionFailureKind.ProtocolFailure);
                }
                IRazerCommandSession session;
                try
                {
                    session = new WindowsRazerHidSession(native.BindSelected(candidate.Handle), io, guard,
                        TimeProvider.System, lease, ControllerExclusivityPolicy.Strict, TimeSpan.FromMilliseconds(30));
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
                var port = new ExperimentalBladePort(session, context, model, policy,
                    () => identity.ReadExactIdentity() is { Evidence: not null, Failure: null }, capturePowerSource,
                    recordQueries: false,
                    sharedOperationGate: operationGate,
                    sharedLightingOwnership: lightingOwnership);
                try
                {
                    // Correctly correlated selectors identify the interface even when
                    // mutable thermal state is not representable. Setters separately
                    // require confirmed baselines before any hardware write.
                    await port.ValidateControlInterfaceAsync(cancellationToken).ConfigureAwait(false);
                    if (selected is null)
                    {
                        selected = port;
                        port = null;
                    }
                    else
                    {
                        ambiguous = true;
                    }
                }
                catch (IOException)
                {
                    // Keep a bounded typed reason only if no interface can be admitted.
                    lastAdmissionFailure = port?.LastAdmissionFailure ?? lastAdmissionFailure;
                    // This descriptor is not the model's functional control interface.
                }
                finally
                {
                    if (port is not null)
                    {
                        await port.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (ambiguous || selected is null)
            {
                if (selected is null && lastAdmissionFailure is { } failure)
                {
                    policy.Record(failure.Operation, "Query", failure.Outcome,
                        confirmed: failure.ConfirmedValue, failure: failure.FailureCategory);
                }
                policy.Record("InterfaceAdmission", "Query", "Failed",
                    failure: ambiguous ? "MultipleRespondingInterfaces" : "NoRespondingInterface");
                return new(null, HidSessionFailureKind.IdentityBlocked);
            }
            ExperimentalBladePort admitted = selected;
            admitted.EnableQueryRecording();
            selected = null;
            return new(admitted, null);
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                candidate.Handle.Dispose();
            }
            if (selected is not null)
            {
                await selected.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private List<(OpenedCandidateHandle Handle, HidHandleIdentity Identity)> FindCandidates()
    {
        List<(OpenedCandidateHandle Handle, HidHandleIdentity Identity)> matches = [];
        try
        {
            foreach (string path in native.EnumerateDevicePaths())
            {
                HidCandidateOpenResult opened = native.TryOpen(path,
                    WindowsRazerHidEvidenceEvaluator.OperationalOpenParameters);
                if (opened.Candidate is null && opened.Win32Error == 5)
                {
                    opened = native.TryOpen(path, WindowsRazerHidEvidenceEvaluator.MetadataOnlyOpenParameters);
                }
                OpenedCandidateHandle? candidate = opened.Candidate;
                if (candidate is null)
                {
                    continue;
                }
                try
                {
                    HidHandleIdentity descriptor = native.ReadIdentity(candidate);
                    if (IsCandidateDescriptor(model, descriptor))
                    {
                        matches.Add((candidate, descriptor));
                        candidate = null;
                    }
                }
                finally
                {
                    candidate?.Dispose();
                }
            }
            return matches;
        }
        catch
        {
            foreach (var candidate in matches)
            {
                candidate.Handle.Dispose();
            }
            throw;
        }
    }

    internal static bool IsCandidateDescriptor(ExperimentalBladeModel model, HidHandleIdentity descriptor) =>
        descriptor.VendorId == 0x1532 && descriptor.ProductId == model.ProductId
        && descriptor.FeatureReportByteLength == 91;
}
