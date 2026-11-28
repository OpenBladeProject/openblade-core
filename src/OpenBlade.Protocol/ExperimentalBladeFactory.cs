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
    private readonly SemaphoreSlim sharedCommandGate;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ExperimentalLightingOwnershipState lightingOwnership;
    private readonly ExperimentalStartupAnimationState startupAnimationState = new();
    private readonly ExperimentalKeyboardLayoutMetadata keyboardLayoutMetadata = new();
    private readonly ExperimentalVaporChamberAvailability vaporChamberAvailability = new();

    internal ExperimentalBladeFactory(ExperimentalBladeModel model, IHostIdentitySource identity,
        IHidCandidateNative native, IHardwareAccessGuard guard, IHidFeatureIo io,
        Func<PowerSource> capturePowerSource, SemaphoreSlim? sharedCommandGate = null,
        ExperimentalLightingBaselineJournal? lightingJournal = null)
    {
        this.model = model;
        hyperBoostSkuAdmission = HyperBoostSystemSkuPolicy.Evaluate(model.ProductId, null);
        this.identity = identity;
        this.native = native;
        this.guard = guard;
        this.io = io;
        this.capturePowerSource = capturePowerSource;
        // Battery, lighting, admission, and recovery share this singleton factory.
        // A native operation may outlive the provider's managed operation lock.
        this.sharedCommandGate = sharedCommandGate ?? new(1, 1);
        policy = new(model);
        if (model.ProductId != 0x02C7 && !ExperimentalBladePort.SupportsReviewedMcuKeyboardFrames(model))
        {
            // This journal admits the reviewed MCU active-bank baseline contract:
            // KI8110 and the four 2026-11-19 legacy-setter frame reviews. Older
            // frame backends retain their separately reviewed saved-bank and
            // Spectrum restoration semantics; do not apply this parser to them.
            lightingOwnership = new();
            return;
        }
        lightingJournal ??= new(model);
        ExperimentalLightingBaselineJournalLoadResult journal = lightingJournal.Load();
        lightingOwnership = new()
        {
            Journal = lightingJournal,
            JournalInvalid = journal.Invalid,
            Baseline = journal.Baseline,
            Ownership = journal.Invalid || journal.Baseline is not null
                ? (int)LightingRendererOwnershipState.Faulted : 0,
            // A record from an earlier run may follow a [0,5,8] selection.
            McuCustomFrameMayRemain = ExperimentalBladePort.SupportsReviewedMcuKeyboardFrames(model)
                && journal.Baseline is not null,
        };
    }

    public string EvidenceSource => model.EvidenceSource;

    internal ExperimentalLightingOwnershipState LightingOwnership => lightingOwnership;

    internal string LaptopControlRecoveryIdentity => $"{model.ModelNumber}|1532:{model.ProductId:X4}";

    internal bool AdmitsNvidiaLaptopGpuForClockOffsets =>
        ExperimentalBladePort.AdmitsNvidiaLaptopGpuForClockOffsets(model);

    // The SKU admission of the last admitted session. Before one exists it is
    // NotApplicable, so no entry restriction is reported; each port still checks
    // its own SKU admission before an entry.
    private volatile HyperBoostSystemSkuAdmission hyperBoostSkuAdmission;

    // Write-pause recovery publishes these factory capabilities. The keyboard-layout
    // result belongs to the identity that the latest session, including recovery's
    // own fresh session, admitted; a changed identity clears it before a new read.
    public DeviceCapabilities Capabilities => ExperimentalBladePort.WithKeyboardLayoutMetadata(
        ExperimentalBladePort.WithLocalDimmingAdmission(
            ExperimentalBladePort.WithConfirmedExperimentalFanRange(
                ExperimentalBladePort.WithStartupReadAdmission(
                    ExperimentalBladePort.CreateCapabilities(model, policy.WritesAllowed,
                        hyperBoostSkuAdmission), startupAnimationState),
                model, policy.ConfirmedFanRanges),
            model, policy.WritesAllowed, policy.MiniLedPanel),
        model.ProductId == 0x02A0 ? keyboardLayoutMetadata.Value : null,
        model.ProductId == 0x02C5 ? keyboardLayoutMetadata.Value : null);

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
        ExperimentalRecoveryScope recoveryScope = policy.RecoveryScope;
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
            if (!policy.TryCompleteRecovery(recoveryScope, () => acceptObservedState(observed),
                localDimmingObserved: observed.LocalDimmingActive is not null))
            {
                throw new CapabilityUnavailableException("The confirmed state could not be reconciled for recovery.");
            }
            // Acceptance synchronously commits reconciliation under the runtime gate.
            // No asynchronous work or fallible admission may split that commit.
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

    public static ExperimentalBladeFactory? CreateForCurrentHost(Func<PowerSource> capturePowerSource, MailboxLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(capturePowerSource);
        ExperimentalBladeModel? model = ExperimentalBladeModels.FindCurrentHost();
        if (model is null)
        {
            return null;
        }
        var registry = new WindowsRegistryValueSource();
        var native = new WindowsHidNative(lifetime);
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
        ExperimentalBladePort? pending = null;
        try
        {
            HidSessionResult<IProductionBatteryPort> opened =
                await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
            if (opened.Value is not ExperimentalBladePort port || opened.Failure is not null)
            {
                return opened;
            }
            pending = port;
            try
            {
                // Each exact-model read returns immediately for every other product.
                await port.InitializeBlade18KeyboardLayoutAsync(cancellationToken).ConfigureAwait(false);
                await port.InitializeBlade14KeyboardEditionAsync(cancellationToken).ConfigureAwait(false);
                await port.InitializeLocalDimmingPanelAsync(cancellationToken).ConfigureAwait(false);
                // Each exact-model read returns immediately for every other product.
                await port.InitializeRz090529VaporChamberAvailabilityAsync(cancellationToken).ConfigureAwait(false);
                if (!port.IsRecoverySessionHealthy)
                {
                    throw new IOException("An optional keyboard-layout or display-panel read left an unhealthy session.");
                }
            }
            catch (IOException) when (!port.IsRecoverySessionHealthy)
            {
                // Layout, panel and LED38 availability are optional metadata, not admission for other controls.
                // A rejected GET poisons its session: drain it before one complete fresh
                // admission, without repeating the failed metadata query or guessing a layout.
                pending = null;
                await port.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!port.IsRecoverySessionDrained)
                {
                    throw new IOException("The optional keyboard-layout session did not drain.");
                }
                return await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            pending = null;
            return opened;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Admission uses GET commands only. A canceled probe cannot have applied a control write.
            return new(null, HidSessionFailureKind.Canceled);
        }
        finally
        {
            if (pending is not null)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<HidSessionResult<IProductionBatteryPort>> OpenCoreAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(null, HidSessionFailureKind.Canceled);
        }
        if (identity.ReadExactIdentity() is not { Evidence: { } hostEvidence, Failure: null })
        {
            return new(null, HidSessionFailureKind.IdentityBlocked);
        }
        HardwareAccessDecision access = guard.Check();
        if (access.DetectionFailure is not null || access.Conflicts.Count != 0)
        {
            return new(null, access.DetectionFailure is not null
                ? HidSessionFailureKind.ConflictDetectionFailed : HidSessionFailureKind.ConflictBlocked);
        }
        // The SKU is diagnostic host metadata; only HyperBoost's vendor recheck reads it.
        string? systemSku = hostEvidence.Sku;

        List<(OpenedCandidateHandle Handle, HidHandleIdentity Identity)> candidates = FindCandidates();
        ExperimentalBladePort? selected = null;
        HidHandleIdentity selectedIdentity = default;
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
                    var rawSession = new WindowsRazerHidSession(native.BindSelected(candidate.Handle), io, guard,
                        TimeProvider.System, lease, ControllerExclusivityPolicy.Strict, TimeSpan.FromMilliseconds(30),
                        queryDiagnostics: FirmwareQueryDiagnostics.Process);
                    session = new RazerCommandSessionGate(rawSession, sharedCommandGate, ownsInner: true);
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
                    sharedLightingOwnership: lightingOwnership,
                    coolingPadDiscovery: new WindowsHyperBoostEligibilitySource(native),
                    sharedStartupAnimationState: startupAnimationState,
                    systemSku: systemSku);
                try
                {
                    // Correctly correlated selectors identify the interface even when
                    // mutable thermal state is not representable. Setters separately
                    // require confirmed baselines before any hardware write.
                    await port.ValidateControlInterfaceAsync(cancellationToken).ConfigureAwait(false);
                    if (selected is null)
                    {
                        selected = port;
                        selectedIdentity = candidate.Identity;
                        port = null;
                    }
                    else
                    {
                        ambiguous = true;
                    }
                }
                catch (NativeDeviceReadException exception)
                    when (exception.Diagnostic?.Phase == HidSessionFailurePhase.SharedGateAdmission)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A shared factory lock cannot be resolved by probing another
                    // interface, and contention is not evidence of wrong hardware.
                    policy.Record("InterfaceAdmission", "Query", "Failed",
                        failure: $"{exception.Failure}/{exception.Diagnostic.Value.Phase}");
                    return new(null, exception.Failure) { Diagnostic = exception.Diagnostic };
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
            // Key the optional layout read to this session's fresh host and exact HID
            // identity, including its version number, so a changed device reads again.
            admitted.BindKeyboardLayoutMetadata(keyboardLayoutMetadata, new(hostEvidence, selectedIdentity));
            hyperBoostSkuAdmission = admitted.HyperBoostSkuAdmission;
            if (admitted.HyperBoostSkuAdmission != HyperBoostSystemSkuAdmission.NotApplicable)
            {
                // Deduplicated query record: why HyperBoost entry is or is not offered.
                // Exit and restoration never depend on it.
                policy.Record("HyperBoostSystemSku", "Query",
                    HyperBoostSystemSkuPolicy.AllowsEntry(admitted.HyperBoostSkuAdmission) ? "Confirmed" : "Blocked",
                    confirmed: admitted.HyperBoostSkuDiagnostic,
                    failure: HyperBoostSystemSkuPolicy.AllowsEntry(admitted.HyperBoostSkuAdmission)
                        ? null : admitted.HyperBoostSkuAdmission.ToString());
            }
            // Key the optional LED38 probe result to this session's fresh host and exact
            // HID identity, including its version number, so a changed device probes again.
            admitted.BindVaporChamberAvailability(vaporChamberAvailability, new(hostEvidence, selectedIdentity));
            // Startup animation is queried independently by the service. Temporary full
            // charge is read with battery state. Neither optional probe belongs in the
            // shared port-open path; each control still requires its own real readback.
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
