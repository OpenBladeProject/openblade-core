using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal interface ICpuTuningObservationSource
{
    DeviceIdentity? ReadIdentity();
    SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity);
    CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend);
}

internal sealed class CpuTuningController
{
    private readonly ICpuTuningObservationSource source;
    private readonly ICpuTuningOwnershipJournalStore? journalStore;
    private readonly Func<Func<DeviceIdentity, CpuSavedTuningCommand, bool>, ICpuSavedTuningBackend>? backendFactory;
    private readonly ICpuLiveCurveOwnershipJournalStore? curveStore;
    private readonly ICpuLivePowerOwnershipJournalStore? powerStore;
    private readonly CpuLiveCurveTuningTransaction? liveCurve;
    private readonly CpuLivePowerTuningTransaction? livePower;
    private readonly SemaphoreSlim liveGate = new(1, 1);
    private readonly AsyncLocal<LiveAdmission?> liveAdmission = new();
    // A failed CAS may leave no readable marker. Retain that uncertainty across
    // both families for this controller lifetime; disk absence cannot clear it.
    private CpuTuningFeatures liveUncertainty;

    internal CpuTuningController(ICpuTuningObservationSource source,
        ICpuTuningOwnershipJournalStore? journalStore = null,
        Func<Func<DeviceIdentity, CpuSavedTuningCommand, bool>, ICpuSavedTuningBackend>? backendFactory = null,
        ICpuLiveCurveBackend? curveBackend = null, ICpuLiveCurveOwnershipJournalStore? curveStore = null,
        ICpuLivePowerBackend? powerBackend = null, ICpuLivePowerOwnershipJournalStore? powerStore = null)
    {
        if ((curveBackend is null) != (curveStore is null) || (powerBackend is null) != (powerStore is null))
        { throw new ArgumentException("Each live backend requires its durable ownership store."); }
        this.source = source; this.journalStore = journalStore; this.backendFactory = backendFactory;
        this.curveStore = curveStore; this.powerStore = powerStore;
        // Retain transaction state across requests, including uncertain persistence.
        // These wrappers add runtime rejection checks; they grant no native proof.
        // A concrete native provider must additionally capture this operation's
        // lease and bind it into the actual native-boundary proof callbacks.
        if (curveBackend is not null)
        { liveCurve = new(new RuntimeCurveBackend(curveBackend, liveAdmission), curveStore!); }
        if (powerBackend is not null)
        { livePower = new(new RuntimePowerBackend(powerBackend, liveAdmission), powerStore!); }
    }
    internal bool HasSavedBackend => journalStore is not null;

    internal async Task<CpuTuningStatus> ObserveAsync(bool useInstalledBackend, CancellationToken token)
    {
        var observed = await CpuFirmwareReadScheduler.RunAsync(() => Observe(useInstalledBackend, token),
            TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        if (journalStore is null || observed.Identity is null || observed.Saved is null) { return observed.Status; }
        CpuSavedTuningTransactionResult ownership = await CreateTransaction(static (_, _) => false)
            .ObserveOwnershipAsync(observed.Identity, observed.Saved, token).ConfigureAwait(false);
        return observed.Status with
        {
            PendingBoot = ownership.PendingBoot,
            RestorePending = ownership.RestorePending,
            FirmwareRecovery = ownership.Recovery,
        };
    }

    private (CpuTuningStatus Status, DeviceIdentity? Identity, SavedCpuFirmwareReadResult? Saved)
        Observe(bool useInstalledBackend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly,
            CpuTuningBackendAvailability.Unavailable);
        DeviceIdentity? identity = source.ReadIdentity();
        token.ThrowIfCancellationRequested();
        if (identity is null)
        {
            return (new(null, null, unavailable, unavailable, CpuTuningPendingBoot.Unknown, false), null, null);
        }

        SavedCpuFirmwareReadResult saved = source.ReadSaved(identity);
        token.ThrowIfCancellationRequested();
        CpuSavedConfiguration? decoded = saved.Current is { } current
            ? new(current.PowerLimitMilliwatts, current.PolicyMode, current.PowerLimitMode,
                DecodeCurve(current))
            : null;
        CpuTuningBackendStatus firmware = DescribeFirmwareBackend(saved);
        CpuTuningBackendStatus live = source.InspectInstalled(identity, useInstalledBackend) with
        {
            // Passive dependency discovery is not native query/write admission.
            Access = DeviceFeatureAccess.ObservedOnly,
        };
        token.ThrowIfCancellationRequested();
        // Saved observations cannot establish active settings, ownership, a
        // pending OpenBlade write, or successful boot activation.
        return (new(decoded, null, firmware, live, CpuTuningPendingBoot.Unknown, false), identity, saved);
    }

    internal async Task<ControlOperationResult> ApplySavedAsync(CpuTuningSettings settings,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, CancellationToken token) =>
        await MutateSavedAsync(settings, freshRuntimeAdmission, restoring: false, token).ConfigureAwait(false);

    internal async Task<ControlOperationResult> RestoreSavedAsync(
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, CancellationToken token) =>
        await MutateSavedAsync(null, freshRuntimeAdmission, restoring: true, token).ConfigureAwait(false);

    private CpuSavedTuningTransaction CreateTransaction(
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission) => new(
            backendFactory?.Invoke(freshRuntimeAdmission) ?? new WindowsSavedCpuTuningBackend(source,
                (identity, command) => WindowsSavedCpuTuningBackend.IsProductionCommandAdmitted(command)
                    && freshRuntimeAdmission(identity, command)),
            journalStore ?? throw new InvalidOperationException("CPU ownership storage is unavailable."));

    private async Task<ControlOperationResult> MutateSavedAsync(CpuTuningSettings? settings,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, bool restoring,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (journalStore is null)
        { return new(ControlOperationStatus.Blocked, "CPU ownership storage is unavailable."); }
        DeviceIdentity? identity = source.ReadIdentity();
        if (!CpuSavedTuningPlan.IsExactIdentity(identity))
        { return new(ControlOperationStatus.Blocked, "The saved CPU firmware identity is not admitted."); }
        CpuSavedTuningTransaction transaction = CreateTransaction(freshRuntimeAdmission);
        CpuSavedTuningTransactionResult result = restoring
            ? await transaction.RestoreAsync(identity!, token).ConfigureAwait(false)
            : await transaction.ApplyAsync(identity!, settings!, token).ConfigureAwait(false);
        // Reload the durable journal, using only this operation's readback.
        // Missing readback must not fall back to an older UI observation.
        CpuSavedTuningTransactionResult ownership = await transaction.ObserveOwnershipAsync(identity!,
            result.Observation ?? new(SavedCpuFirmwareReadStatus.Unavailable, null, null, 0, 0),
            token).ConfigureAwait(false);
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly,
            CpuTuningBackendAvailability.Unavailable);
        CpuSavedConfiguration? saved = result.Observation?.Current is { } current
            ? new(current.PowerLimitMilliwatts, current.PolicyMode, current.PowerLimitMode, DecodeCurve(current)) : null;
        var status = new CpuTuningStatus(saved, null,
            DescribeFirmwareBackend(result.Observation),
            unavailable, ownership.PendingBoot, ownership.RestorePending)
        { FirmwareRecovery = ownership.Recovery };
        ControlOperationStatus operation = result.Outcome switch
        {
            CpuSavedTuningOutcome.Verified or CpuSavedTuningOutcome.Unchanged => ControlOperationStatus.Stored,
            CpuSavedTuningOutcome.Uncertain => ControlOperationStatus.Failed,
            _ => ControlOperationStatus.Blocked,
        };
        string message = result.Outcome switch
        {
            CpuSavedTuningOutcome.Verified => restoring
                ? "The original saved CPU configuration was restored and verified. Live settings remain unconfirmed."
                : "CPU settings were saved and verified for the next boot. Live settings remain unconfirmed.",
            CpuSavedTuningOutcome.Unchanged => result.RestorePending
                ? "Saved CPU settings already match; the original restoration baseline was retained."
                : "Saved CPU settings already match; no ownership was established.",
            CpuSavedTuningOutcome.Uncertain => "CPU submission or readback is uncertain. Further CPU writes are blocked.",
            _ => result.Reason switch
            {
                CpuSavedTuningBlockReason.InvalidJournal => "CPU recovery data is invalid. Further CPU writes are blocked.",
                CpuSavedTuningBlockReason.PendingSubmission => "A prior CPU submission remains unresolved. Further writes are blocked.",
                CpuSavedTuningBlockReason.UnrestorableBaseline => "The saved CPU baseline has no admitted exact restoration route.",
                CpuSavedTuningBlockReason.AdmissionDenied => "CPU commands require attended service write and restoration admission.",
                CpuSavedTuningBlockReason.JournalUnavailable => "CPU recovery data could not be saved. No further command was submitted.",
                _ => "The saved CPU state or write authority changed; further writes are blocked.",
            },
        };
        return new(operation, message) { CpuTuning = status };
    }

    private static CpuTuningBackendStatus DescribeFirmwareBackend(SavedCpuFirmwareReadResult? saved) =>
        new(DeviceFeatureAccess.ObservedOnly, saved?.Status switch
        {
            SavedCpuFirmwareReadStatus.Observed => CpuTuningBackendAvailability.Available,
            SavedCpuFirmwareReadStatus.InvalidData or SavedCpuFirmwareReadStatus.MirrorMismatch => CpuTuningBackendAvailability.Failed,
            _ => CpuTuningBackendAvailability.Unavailable,
        });

    // A future provider must capture this during the initiating operation and
    // repeat it inside its native proof. This check is rejection-only authority.
    internal Func<bool> CaptureLiveRuntimeAdmission(CpuTuningFeatures feature)
    {
        LiveAdmission? captured = liveAdmission.Value;
        return () => captured?.Allows(feature) == true;
    }

    internal bool HasLiveBackend => liveCurve is not null || livePower is not null;

    internal async Task<CpuTuningStatus> ObserveLiveAsync(Func<CpuTuningFeatures, bool> freshAdmission,
        CancellationToken token)
    {
        await liveGate.WaitAsync(token).ConfigureAwait(false);
        using var lease = new LiveAdmission(freshAdmission, token);
        liveAdmission.Value = lease;
        try
        {
            var ownership = await ReadLiveOwnershipAsync(token).ConfigureAwait(false);
            if (ownership.Blocked) { return LiveStatus(null, null, CpuTuningRecoveryState.Blocked, ownership.Features); }
            CpuLiveCurveTransactionResult? curve = liveCurve is null ? null
                : await liveCurve.ObserveOwnershipAsync(token).ConfigureAwait(false);
            RetainUncertainty(curve);
            // Do not start another family after uncertainty or failed admission.
            CpuLivePowerTransactionResult? power = livePower is null || liveUncertainty != CpuTuningFeatures.None
                || curve?.Recovery == CpuLiveCurveRecovery.Blocked
                || !lease.Allows(CpuTuningFeatures.None) ? null
                : await livePower.ObserveOwnershipAsync(token).ConfigureAwait(false);
            RetainUncertainty(power);
            var recovery = liveUncertainty != CpuTuningFeatures.None
                || curve?.Recovery == CpuLiveCurveRecovery.Blocked || power?.Recovery == CpuLivePowerRecovery.Blocked
                ? CpuTuningRecoveryState.Blocked : ownership.Features == CpuTuningFeatures.None
                    ? CpuTuningRecoveryState.None : CpuTuningRecoveryState.RestoreReady;
            return LiveStatus(curve?.Observation, power?.Observation, recovery, ownership.Features | liveUncertainty);
        }
        finally
        {
            try { lease.Dispose(); }
            finally { liveAdmission.Value = null; liveGate.Release(); }
        }
    }

    internal async Task<ControlOperationResult> MutateLiveAsync(CpuTuningSettings? settings, bool restoring,
        Func<CpuTuningFeatures, bool> freshAdmission, CancellationToken token)
    {
        CpuTuningFeatures requested = (settings?.CurveOptimizer is null ? CpuTuningFeatures.None : CpuTuningFeatures.CurveOptimizer)
            | (settings?.PowerLimitWatts is null ? CpuTuningFeatures.None : CpuTuningFeatures.PowerLimit);
        if (!HasLiveBackend || !restoring && (settings is not { IsWellFormed: true, IsConfigured: true }
            || requested == (CpuTuningFeatures.CurveOptimizer | CpuTuningFeatures.PowerLimit)))
        { return new(ControlOperationStatus.Blocked, "Live CPU tuning currently requires one supported feature at a time."); }
        await liveGate.WaitAsync(token).ConfigureAwait(false);
        using var lease = new LiveAdmission(freshAdmission, token);
        liveAdmission.Value = lease;
        try
        {
            var ownership = await ReadLiveOwnershipAsync(token).ConfigureAwait(false);
            if (restoring) { requested = ownership.Features; }
            if (ownership.Blocked || requested is not (CpuTuningFeatures.CurveOptimizer or CpuTuningFeatures.PowerLimit)
                || (ownership.Features & ~requested) != CpuTuningFeatures.None
                || requested == CpuTuningFeatures.CurveOptimizer && liveCurve is null
                || requested == CpuTuningFeatures.PowerLimit && livePower is null
                || !lease.Allows(requested))
            {
                return new(ControlOperationStatus.Blocked, "Live CPU ownership or fresh runtime admission does not permit this operation.")
                {
                    CpuTuning = LiveStatus(null, null, ownership.Features == CpuTuningFeatures.None
                        ? CpuTuningRecoveryState.None : CpuTuningRecoveryState.Blocked, ownership.Features),
                };
            }
            bool verified, uncertain; CpuTuningRecoveryState recovery;
            CpuLiveCurveSnapshot? curve = null; CpuLivePowerPair? power = null;
            if (requested == CpuTuningFeatures.CurveOptimizer)
            {
                var result = restoring ? await liveCurve!.RestoreAsync(token).ConfigureAwait(false)
                    : await liveCurve!.ApplyAllCoreAsync(settings!.CurveOptimizer, token).ConfigureAwait(false);
                RetainUncertainty(result);
                verified = result.Outcome is CpuLiveCurveOutcome.Verified or CpuLiveCurveOutcome.Unchanged;
                uncertain = result.Outcome == CpuLiveCurveOutcome.Uncertain;
                curve = result.Observation;
                recovery = result.Recovery switch
                {
                    CpuLiveCurveRecovery.None => CpuTuningRecoveryState.None,
                    CpuLiveCurveRecovery.RestoreReady => CpuTuningRecoveryState.RestoreReady,
                    _ => CpuTuningRecoveryState.Blocked,
                };
            }
            else
            {
                var result = restoring ? await livePower!.RestoreAsync(token).ConfigureAwait(false)
                    : await livePower!.ApplySelectiveAsync([
                        new(CpuLivePowerLimit.Fast, settings!.PowerLimitWatts!.Value),
                        new(CpuLivePowerLimit.Slow, settings!.PowerLimitWatts!.Value)], token).ConfigureAwait(false);
                RetainUncertainty(result);
                verified = result.Outcome is CpuLivePowerOutcome.Verified or CpuLivePowerOutcome.Unchanged;
                uncertain = result.Outcome == CpuLivePowerOutcome.Uncertain;
                power = result.Observation;
                recovery = result.Recovery switch
                {
                    CpuLivePowerRecovery.None => CpuTuningRecoveryState.None,
                    CpuLivePowerRecovery.RestoreReady => CpuTuningRecoveryState.RestoreReady,
                    _ => CpuTuningRecoveryState.Blocked,
                };
            }
            var retained = await ReadLiveOwnershipAsync(CancellationToken.None).ConfigureAwait(false);
            if (retained.Blocked) { verified = false; recovery = CpuTuningRecoveryState.Blocked; }
            CpuTuningFeatures recoveryFeatures = retained.Features;
            if (uncertain) { recoveryFeatures |= requested; }
            return new(verified ? ControlOperationStatus.Stored : uncertain ? ControlOperationStatus.Failed : ControlOperationStatus.Blocked,
                verified ? restoring ? "The original live CPU setting was restored and verified." : "The live CPU setting was verified."
                    : uncertain ? "Live CPU submission or readback is uncertain. Further live commands are blocked."
                    : "The live CPU operation was blocked; recovery data was retained.")
            { CpuTuning = LiveStatus(curve, power, recovery, recovery == CpuTuningRecoveryState.None ? CpuTuningFeatures.None : recoveryFeatures) };
        }
        finally
        {
            try { lease.Dispose(); }
            finally { liveAdmission.Value = null; liveGate.Release(); }
        }
    }

    private async Task<(bool Blocked, CpuTuningFeatures Features)> ReadLiveOwnershipAsync(CancellationToken token)
    {
        var curve = curveStore is null ? null : await curveStore.LoadAsync(token).ConfigureAwait(false);
        var power = powerStore is null ? null : await powerStore.LoadAsync(token).ConfigureAwait(false);
        CpuTuningFeatures features = CpuTuningFeatures.None;
        if (curve?.Invalid == true || curve?.Journal is { State: not CpuLiveCurveOwnershipState.Restored })
        { features |= CpuTuningFeatures.CurveOptimizer; }
        if (power?.Invalid == true || power?.Journal is { State: not CpuLivePowerOwnershipState.Restored })
        { features |= CpuTuningFeatures.PowerLimit; }
        bool blocked = curve?.Invalid == true || power?.Invalid == true
            || curve?.Journal is { State: CpuLiveCurveOwnershipState.PossibleSubmission or CpuLiveCurveOwnershipState.Blocked }
            || power?.Journal is { State: CpuLivePowerOwnershipState.PossibleSubmission or CpuLivePowerOwnershipState.Blocked };
        return (blocked || liveUncertainty != CpuTuningFeatures.None, features | liveUncertainty);
    }

    private void RetainUncertainty(CpuLiveCurveTransactionResult? result)
    {
        if (result is { Outcome: CpuLiveCurveOutcome.Uncertain } or { Reason: CpuLiveCurveBlockReason.JournalUnavailable })
        { liveUncertainty |= CpuTuningFeatures.CurveOptimizer; }
    }

    private void RetainUncertainty(CpuLivePowerTransactionResult? result)
    {
        if (result is { Outcome: CpuLivePowerOutcome.Uncertain } or { Reason: CpuLivePowerBlockReason.JournalUnavailable })
        { liveUncertainty |= CpuTuningFeatures.PowerLimit; }
    }
    private static CpuTuningStatus LiveStatus(CpuLiveCurveSnapshot? curve, CpuLivePowerPair? power,
        CpuTuningRecoveryState recovery, CpuTuningFeatures features)
    {
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
        // Publish only the independently observed families. Raw fast/slow limits
        // do not establish effective watts or any CO values.
        CpuLiveState? live = curve is null && power is null ? null : new(power is null ? null
            : new(null, power.Fast.Milliwatts, power.Slow.Milliwatts),
            curve?.CoreOffsets.Select(core => new CpuCoreCurveOptimizerValue(core.PhysicalCoreId, core.Offset)).ToArray() ?? []);
        return new(null, live, unavailable, unavailable with
        { Availability = curve is not null || power is not null ? CpuTuningBackendAvailability.Available : CpuTuningBackendAvailability.Unavailable },
            CpuTuningPendingBoot.Unknown, features != CpuTuningFeatures.None)
        { LiveRecovery = new(recovery, features) };
    }

    // Runtime admission expires with its initiating call. AsyncLocal captures the
    // exact lease for late continuations; a later request cannot renew it. Native
    // backends still own the actual worker and repeat their independent proofs.
    private sealed class LiveAdmission(Func<CpuTuningFeatures, bool> admit, CancellationToken token) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        private int disposed;
        internal CancellationToken Token => cancellation.Token;
        private int revoked;
        internal bool Allows(CpuTuningFeatures feature)
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref revoked) != 0 || token.IsCancellationRequested)
            { return false; }
            bool admitted;
            try { admitted = admit(feature); }
            catch { Interlocked.Exchange(ref revoked, 1); return false; }
            if (admitted && Volatile.Read(ref disposed) == 0 && Volatile.Read(ref revoked) == 0
                && !token.IsCancellationRequested) { return true; }
            Interlocked.Exchange(ref revoked, 1);
            return false;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
            try { cancellation.Cancel(); } finally { cancellation.Dispose(); }
        }
    }

    private sealed class RuntimeCurveBackend(ICpuLiveCurveBackend backend, AsyncLocal<LiveAdmission?> current) : ICpuLiveCurveBackend
    {
        public async Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.None) != true) { return new(null, null); }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            var result = await backend.ObserveAsync(cancellation.Token).ConfigureAwait(false);
            return lease.Allows(CpuTuningFeatures.None) ? result : new(null, null);
        }
        public async Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CancellationToken token)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.CurveOptimizer) != true) { return false; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            return await backend.CheckWriteAdmissionAsync(context, command, purpose, cancellation.Token).ConfigureAwait(false)
                && lease.Allows(CpuTuningFeatures.CurveOptimizer);
        }
        public async Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.CurveOptimizer) != true) { return CpuLiveCurveSubmission.NotSubmitted; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            var result = await backend.SendAsync(context, command, purpose, expectedBefore, cancellation.Token).ConfigureAwait(false);
            return result == CpuLiveCurveSubmission.NotSubmitted || lease.Allows(CpuTuningFeatures.CurveOptimizer)
                ? result : CpuLiveCurveSubmission.Unknown;
        }
    }
    private sealed class RuntimePowerBackend(ICpuLivePowerBackend backend, AsyncLocal<LiveAdmission?> current) : ICpuLivePowerBackend
    {
        public async Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.None) != true) { return new(null, null, null, null, null); }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            var result = await backend.ObserveAsync(cancellation.Token).ConfigureAwait(false);
            return lease.Allows(CpuTuningFeatures.None) ? result : new(null, null, null, null, null);
        }
        public async Task<bool> CheckWriteAdmissionAsync(CpuLivePowerContext context, CpuLivePowerCommand command,
            CpuLivePowerCommandPurpose purpose, CpuLivePowerPair expectedBefore, CpuLivePowerPair original, CancellationToken token)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.PowerLimit) != true) { return false; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            return await backend.CheckWriteAdmissionAsync(context, command, purpose, expectedBefore, original, cancellation.Token).ConfigureAwait(false)
                && lease.Allows(CpuTuningFeatures.PowerLimit);
        }
        public async Task<CpuLivePowerSubmission> SendAsync(CpuLivePowerContext context, CpuLivePowerNativeRequest request, CancellationToken token)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.PowerLimit) != true) { return CpuLivePowerSubmission.NotSubmitted; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            var result = await backend.SendAsync(context, request, cancellation.Token).ConfigureAwait(false);
            return result == CpuLivePowerSubmission.NotSubmitted || lease.Allows(CpuTuningFeatures.PowerLimit)
                ? result : CpuLivePowerSubmission.Unknown;
        }
    }
    private static CpuCurveOptimizerState? DecodeCurve(Rz090528SavedCpuConfiguration saved)
    {
        if (saved.AllCoreCurveOptimizer is { } allCore)
        {
            return new(CpuCurveOptimizerMode.AllCore, allCore, []);
        }

        // Per-core storage is retained losslessly by the private reader. A
        // public active-core summary additionally needs validated topology.
        return null;
    }
}

internal sealed class WindowsCpuTuningObservationSource : ICpuTuningObservationSource
{
    public DeviceIdentity? ReadIdentity()
    {
        var registry = new WindowsRegistryValueSource();
        HostIdentityReadResult host = new WindowsHostIdentitySource(registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity, compatibilityPolicy: null).ReadExactIdentity();
        return host is { Failure: null, Evidence: { } evidence }
            ? new("RZ09-0528", 0x1532, 0x02C6, evidence.BiosVersion)
            : null;
    }

    public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity)
    {
        if (identity.BiosVersion != "2.02")
        {
            return new(SavedCpuFirmwareReadStatus.UnsupportedIdentity, null, null, 0, 0);
        }

        try
        {
            // Duplicate the caller's token: enable the read prerequisite only
            // on a temporary impersonation token, never on the service token.
            using WindowsIdentity current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
            if (!DuplicateTokenEx(current.AccessToken, 0x2C, 0, 2, 2, out SafeAccessTokenHandle temporary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using (temporary)
            {
                if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out Luid privilege))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var privileges = new TokenPrivileges { Count = 1, Id = privilege, Attributes = 2 };
                if (!AdjustTokenPrivileges(temporary, false, ref privileges, 0, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 0) { throw new Win32Exception(error); }
                return WindowsIdentity.RunImpersonated(temporary,
                    () => new Rz090528SavedCpuFirmwareReader().Read(identity));
            }
        }
        catch (Win32Exception exception)
        {
            return new(SavedCpuFirmwareReadStatus.Unavailable, null, null,
                exception.NativeErrorCode, exception.NativeErrorCode);
        }
    }

    public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) =>
        new Rz090528InstalledCpuBackendInspector().Inspect(useInstalledBackend, identity);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Id; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access,
        nint attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid id);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disable, ref TokenPrivileges privileges,
        uint length, nint previous, nint returned);
}
