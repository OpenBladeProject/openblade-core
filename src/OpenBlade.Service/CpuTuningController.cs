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
    private readonly Guid serviceInstance = Guid.NewGuid();
    // A failed CAS may leave no readable marker. Retain that uncertainty across
    // both families for this controller lifetime; disk absence cannot clear it.
    private CpuTuningFeatures liveUncertainty;
    private bool savedPersistenceUncertain;
    private CpuTuningFeatures savedRecoveryFeatures;
    private CpuTuningStatus? savedUncertainStatus;

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
        if (savedPersistenceUncertain) { return SavedPersistenceVeto(observed.Status); }
        if (journalStore is null || observed.Identity is null || observed.Saved is null) { return observed.Status; }
        CpuSavedTuningTransactionResult ownership = await CreateTransaction(static (_, _) => false)
            .ObserveOwnershipAsync(observed.Identity, observed.Saved, token).ConfigureAwait(false);
        RetainSavedUncertainty(ownership, CpuTuningFeatures.None);
        var status = observed.Status with
        {
            PendingBoot = ownership.PendingBoot,
            RestorePending = ownership.RestorePending,
            FirmwareRecovery = ownership.Recovery,
        };
        if (savedPersistenceUncertain) { return SavedPersistenceVeto(status); }
        savedRecoveryFeatures = ownership.Recovery.Features;
        return status;
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
        CpuTuningBackendStatus firmware = DescribeFirmwareBackend(identity, saved);
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
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, CancellationToken token,
        CpuSavedAdmissionDiagnostic? diagnostic = null) =>
        await MutateSavedAsync(settings, freshRuntimeAdmission, restoring: false, token, diagnostic).ConfigureAwait(false);

    internal async Task<ControlOperationResult> RestoreSavedAsync(
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, CancellationToken token,
        CpuSavedAdmissionDiagnostic? diagnostic = null) =>
        await MutateSavedAsync(null, freshRuntimeAdmission, restoring: true, token, diagnostic).ConfigureAwait(false);

    private CpuSavedTuningTransaction CreateTransaction(
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission,
        CpuSavedAdmissionDiagnostic? diagnostic = null) => new(
            backendFactory?.Invoke(freshRuntimeAdmission) ?? new WindowsSavedCpuTuningBackend(source,
                (identity, command) => WindowsSavedCpuTuningBackend.IsProductionCommandAdmitted(command)
                    && freshRuntimeAdmission(identity, command), diagnostic),
            journalStore ?? throw new InvalidOperationException("CPU ownership storage is unavailable."));

    private async Task<ControlOperationResult> MutateSavedAsync(CpuTuningSettings? settings,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, bool restoring,
        CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic)
    {
        token.ThrowIfCancellationRequested();
        if (journalStore is null)
        { return new(ControlOperationStatus.Blocked, "CPU ownership storage is unavailable."); }
        if (savedPersistenceUncertain)
        {
            return new(ControlOperationStatus.Blocked, "Saved CPU journal persistence is uncertain. Further saved writes are blocked.")
            { CpuTuning = SavedPersistenceVeto(savedUncertainStatus) };
        }
        DeviceIdentity? identity = source.ReadIdentity();
        if (!CpuSavedTuningPlan.IsExactIdentity(identity))
        { return new(ControlOperationStatus.Blocked, "The saved CPU firmware identity is not admitted."); }
        diagnostic ??= new CpuSavedAdmissionDiagnostic();
        CpuSavedTuningTransaction transaction = CreateTransaction(freshRuntimeAdmission, diagnostic);
        CpuSavedTuningTransactionResult result = restoring
            ? await transaction.RestoreAsync(identity!, token).ConfigureAwait(false)
            : await transaction.ApplyAsync(identity!, settings!, token).ConfigureAwait(false);
        RetainSavedUncertainty(result,
            (settings?.CurveOptimizer is null ? CpuTuningFeatures.None : CpuTuningFeatures.CurveOptimizer)
            | (settings?.PowerLimitWatts is null ? CpuTuningFeatures.None : CpuTuningFeatures.PowerLimit));
        // Reload the durable journal, using only this operation's readback.
        // Missing readback must not fall back to an older UI observation.
        CpuSavedTuningTransactionResult ownership = await transaction.ObserveOwnershipAsync(identity!,
            result.Observation ?? new(SavedCpuFirmwareReadStatus.Unavailable, null, null, 0, 0),
            token).ConfigureAwait(false);
        RetainSavedUncertainty(ownership, CpuTuningFeatures.None);
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly,
            CpuTuningBackendAvailability.Unavailable);
        CpuSavedConfiguration? saved = result.Observation?.Current is { } current
            ? new(current.PowerLimitMilliwatts, current.PolicyMode, current.PowerLimitMode, DecodeCurve(current)) : null;
        var status = new CpuTuningStatus(saved, null,
            DescribeFirmwareBackend(identity, result.Observation),
            unavailable, ownership.PendingBoot, ownership.RestorePending)
        { FirmwareRecovery = ownership.Recovery };
        if (savedPersistenceUncertain) { status = SavedPersistenceVeto(status); }
        else { savedRecoveryFeatures = ownership.Recovery.Features; }
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
        if (operation == ControlOperationStatus.Blocked && result.Reason == CpuSavedTuningBlockReason.AdmissionDenied
            && diagnostic.FirstFailure is { } failure)
        {
            message += $" Admission failed at: {failure}.";
        }
        return new(savedPersistenceUncertain ? ControlOperationStatus.Blocked : operation,
            savedPersistenceUncertain ? "Saved CPU journal persistence is uncertain. Further saved writes are blocked." : message)
        { CpuTuning = status };
    }

    private void RetainSavedUncertainty(CpuSavedTuningTransactionResult result, CpuTuningFeatures requested)
    {
        if (result.Reason != CpuSavedTuningBlockReason.JournalUnavailable) { return; }
        savedPersistenceUncertain = true;
        savedRecoveryFeatures |= result.Recovery.Features | requested;
    }

    private CpuTuningStatus SavedPersistenceVeto(CpuTuningStatus? status)
    {
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
        return savedUncertainStatus = (status ?? new(null, null, unavailable, unavailable, CpuTuningPendingBoot.Unknown, true)) with
        {
            RestorePending = true,
            FirmwareRecovery = new(CpuTuningRecoveryState.Blocked, savedRecoveryFeatures),
        };
    }

    private CpuTuningBackendStatus DescribeFirmwareBackend(DeviceIdentity? identity, SavedCpuFirmwareReadResult? saved)
    {
        bool pair = saved is
        { Status: SavedCpuFirmwareReadStatus.Observed, CurrentError: 0, LastError: 0, Current: not null, Last: not null }
            && saved.HasRestoreBaseline && saved.Current.CopyBytes() is { Length: 800 } current
            && current.AsSpan().SequenceEqual(saved.Last.CopyBytes());
        bool available = pair && CpuSavedTuningPlan.IsExactIdentity(identity);
        // Availability does not admit a particular baseline's reverse route;
        // the transaction checks both original images and the complete plan.
        return new(journalStore is not null && available && CpuSavedSemanticPolicy.MatchesKnownAnswers()
                ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly,
            available ? CpuTuningBackendAvailability.Available : saved?.Status switch
            {
                SavedCpuFirmwareReadStatus.Observed or SavedCpuFirmwareReadStatus.InvalidData
                    or SavedCpuFirmwareReadStatus.MirrorMismatch => CpuTuningBackendAvailability.Failed,
                _ => CpuTuningBackendAvailability.Unavailable,
            });
    }

    // Providers capture the initiating operation, never a replaceable bool.
    // A late native worker cannot borrow a subsequent request's admission.
    internal Func<bool> CaptureLiveRuntimeAdmission(CpuTuningFeatures feature, CancellationToken operationToken = default)
    {
        LiveAdmission? captured = liveAdmission.Value;
        return () => captured?.Allows(feature, operationToken) == true;
    }

    internal CpuLiveGetterAdmission? BeginLiveGetter(CancellationToken token) => liveAdmission.Value?.BeginGetter(token);

    internal CpuLiveRuntimeLifetime? CaptureLiveRuntimeLifetime() => liveAdmission.Value?.Lifetime;
    internal object? CaptureLiveRuntimeRequest() => liveAdmission.Value;
    internal CpuSavedAdmissionDiagnostic? CaptureLiveObservationDiagnostic() => liveAdmission.Value?.Diagnostic;

    internal bool HasLiveBackend => liveCurve is not null || livePower is not null;

    internal Task<CpuTuningStatus> ObserveLiveAsync(Func<CpuTuningFeatures, bool> freshAdmission,
        CancellationToken token, long lowPowerGeneration = 0, long powerSourceGeneration = 0,
        CpuSavedAdmissionDiagnostic? diagnostic = null) =>
        ObserveLiveAsync((feature, _) => freshAdmission(feature), token, lowPowerGeneration, powerSourceGeneration, diagnostic);

    internal async Task<CpuTuningStatus> ObserveLiveAsync(Func<CpuTuningFeatures, CancellationToken, bool> freshAdmission,
        CancellationToken token, long lowPowerGeneration = 0, long powerSourceGeneration = 0,
        CpuSavedAdmissionDiagnostic? diagnostic = null,
        Func<CpuTuningFeatures, CancellationToken, Func<bool>, CpuLiveGetterAdmission>? getterAdmission = null)
    {
        await liveGate.WaitAsync(token).ConfigureAwait(false);
        using var lease = new LiveAdmission(feature => freshAdmission(feature, token), token,
            new(serviceInstance, lowPowerGeneration, powerSourceGeneration), diagnostic, freshAdmission, getterAdmission);
        liveAdmission.Value = lease;
        try
        {
            var ownership = await ReadLiveOwnershipAsync(token).ConfigureAwait(false);
            if (ownership.Blocked)
            {
                if (diagnostic is not null) { diagnostic.LiveObservationReason = "ownership blocked: " + ownership.Features; }
                return LiveStatus(null, null, CpuTuningRecoveryState.Blocked, ownership.Features);
            }
            CpuLiveCurveTransactionResult? curve = liveCurve is null ? null
                : await liveCurve.ObserveOwnershipAsync(token, diagnostic).ConfigureAwait(false);
            if (diagnostic is not null && curve?.Reason is { } curveReason && curveReason != CpuLiveCurveBlockReason.None)
            {
                diagnostic.LiveObservationReason = "curve transaction " + curveReason
                    + diagnostic.LiveObservationTimeoutContext;
            }
            RetainUncertainty(curve);
            // Do not start another family after uncertainty or failed admission.
            CpuLivePowerTransactionResult? power = livePower is null || liveUncertainty != CpuTuningFeatures.None
                || curve?.Recovery == CpuLiveCurveRecovery.Blocked
                || !lease.Allows(CpuTuningFeatures.None) ? null
                : await livePower.ObserveOwnershipAsync(token, diagnostic).ConfigureAwait(false);
            RetainUncertainty(power);
            if (diagnostic is not null && diagnostic.LiveObservationReason is null && power?.Reason is { } powerReason
                && powerReason != CpuLivePowerBlockReason.None)
            { diagnostic.LiveObservationReason = "power transaction " + powerReason + diagnostic.LiveObservationTimeoutContext; }
            var recovery = liveUncertainty != CpuTuningFeatures.None
                || curve?.Recovery == CpuLiveCurveRecovery.Blocked || power?.Recovery == CpuLivePowerRecovery.Blocked
                ? CpuTuningRecoveryState.Blocked : ownership.Features == CpuTuningFeatures.None
                    ? CpuTuningRecoveryState.None : CpuTuningRecoveryState.RestoreReady;
            return LiveStatus(curve?.Observation, power?.Observation, recovery, ownership.Features | liveUncertainty,
                liveCurve is not null && curve?.Observation is not null && recovery != CpuTuningRecoveryState.Blocked
                    && lease.CanPresentCurveWrite());
        }
        finally
        {
            try { lease.Dispose(); }
            finally { liveAdmission.Value = null; liveGate.Release(); }
        }
    }

    internal async Task<ControlOperationResult> MutateLiveAsync(CpuTuningSettings? settings, bool restoring,
        Func<CpuTuningFeatures, bool> freshAdmission, CancellationToken token,
        long lowPowerGeneration = 0, long powerSourceGeneration = 0,
        Func<CpuTuningFeatures, CancellationToken, Func<bool>, CpuLiveGetterAdmission>? getterAdmission = null)
    {
        CpuTuningFeatures requested = (settings?.CurveOptimizer is null ? CpuTuningFeatures.None : CpuTuningFeatures.CurveOptimizer)
            | (settings?.PowerLimitWatts is null ? CpuTuningFeatures.None : CpuTuningFeatures.PowerLimit);
        if (!HasLiveBackend || !restoring && settings is not { IsWellFormed: true, IsConfigured: true })
        { return new(ControlOperationStatus.Blocked, "Live CPU tuning requires supported settings and an admitted backend."); }
        await liveGate.WaitAsync(token).ConfigureAwait(false);
        using var lease = new LiveAdmission(freshAdmission, token, new(serviceInstance, lowPowerGeneration, powerSourceGeneration), getterAdmission: getterAdmission);
        liveAdmission.Value = lease;
        CpuLiveCurveTuningTransaction.PreparedOperation? curveOperation = null;
        CpuLivePowerTuningTransaction.PreparedOperation? powerOperation = null;
        CpuLiveCurveSnapshot? curve = null;
        CpuLivePowerPair? power = null;
        bool uncertain = false;
        bool confirmedPeerLoss = false;
        try
        {
            var ownership = await ReadLiveOwnershipAsync(token).ConfigureAwait(false);
            if (restoring) { requested = ownership.Features; }
            CpuTuningFeatures required = requested | ownership.Features;
            lease.Require(required);
            if (ownership.Blocked || requested == CpuTuningFeatures.None
                || required.HasFlag(CpuTuningFeatures.CurveOptimizer) && liveCurve is null
                || required.HasFlag(CpuTuningFeatures.PowerLimit) && livePower is null
                || !lease.Allows(required)) { return await FinishAsync(false).ConfigureAwait(false); }

            // Both complete plans are durably prepared before either sends. A
            // peer owned by a prior edit is retained and validated without edits.
            if (restoring)
            {
                if (!await PrepareCurveAsync().ConfigureAwait(false) || !await PreparePowerAsync().ConfigureAwait(false))
                { return await FinishAsync(false).ConfigureAwait(false); }
            }
            else if (!await PreparePowerAsync().ConfigureAwait(false) || !await PrepareCurveAsync().ConfigureAwait(false))
            { return await FinishAsync(false).ConfigureAwait(false); }
            if (curveOperation is not null && powerOperation is not null
                && !CompatibleContexts(curveOperation.Context, powerOperation.Context))
            { return await FinishAsync(false).ConfigureAwait(false); }
            if (!await CheckCurveAsync(token).ConfigureAwait(false) || !await CheckPowerAsync(token).ConfigureAwait(false))
            { return await FinishAsync(false).ConfigureAwait(false); }

            // Family order is explicit; an incomplete family stops the sequence.
            // There is no compensation, automatic restore or restart replay.
            bool completed = restoring
                ? await ExecuteCurveAsync().ConfigureAwait(false) && await ExecutePowerAsync().ConfigureAwait(false)
                : await ExecutePowerAsync().ConfigureAwait(false) && await ExecuteCurveAsync().ConfigureAwait(false);
            if (completed)
            { completed = await CheckCurveAsync(token).ConfigureAwait(false) && await CheckPowerAsync(token).ConfigureAwait(false); }
            return await FinishAsync(completed).ConfigureAwait(false);

            async Task<bool> PrepareCurveAsync()
            {
                if (!required.HasFlag(CpuTuningFeatures.CurveOptimizer)) { return true; }
                if (!lease.Allows(required)) { return false; }
                var prepared = restoring ? await liveCurve!.PrepareRestoreAsync(token).ConfigureAwait(false)
                    : await liveCurve!.PrepareApplyAsync(settings!.CurveOptimizer, token).ConfigureAwait(false);
                RecordCurve(prepared.Result); curveOperation = prepared.Operation;
                return curveOperation is not null && lease.Allows(required);
            }
            async Task<bool> PreparePowerAsync()
            {
                if (!required.HasFlag(CpuTuningFeatures.PowerLimit)) { return true; }
                if (!lease.Allows(required)) { return false; }
                var prepared = restoring ? await livePower!.PrepareRestoreAsync(token).ConfigureAwait(false)
                    : await livePower!.PrepareApplyAsync(settings!.PowerLimitWatts is { } watts
                        ? [new(CpuLivePowerLimit.Fast, watts), new(CpuLivePowerLimit.Slow, watts)] : [], token).ConfigureAwait(false);
                RecordPower(prepared.Result); powerOperation = prepared.Operation;
                return powerOperation is not null && lease.Allows(required);
            }
            async Task<bool> CheckCurveAsync(CancellationToken checkToken)
            {
                if (!lease.Allows(required) || liveUncertainty != CpuTuningFeatures.None) { return false; }
                if (curveOperation is null) { return true; }
                var result = await liveCurve!.ValidatePreparedAsync(curveOperation, checkToken).ConfigureAwait(false);
                RecordCurve(result);
                return result.Outcome == CpuLiveCurveOutcome.Unchanged && lease.Allows(required);
            }
            async Task<bool> CheckPowerAsync(CancellationToken checkToken)
            {
                if (!lease.Allows(required) || liveUncertainty != CpuTuningFeatures.None) { return false; }
                if (powerOperation is null) { return true; }
                var result = await livePower!.ValidatePreparedAsync(powerOperation, checkToken).ConfigureAwait(false);
                RecordPower(result);
                return result.Outcome == CpuLivePowerOutcome.Unchanged && lease.Allows(required);
            }
            async Task<bool> ExecuteCurveAsync()
            {
                if (!requested.HasFlag(CpuTuningFeatures.CurveOptimizer)) { return true; }
                if (!lease.Allows(required)) { return false; }
                // Called while CO holds its gate: validate only the PPT peer.
                var result = await liveCurve!.ExecutePreparedAsync(curveOperation!, CheckPowerAsync, token, powerOperation?.PeerState).ConfigureAwait(false);
                RecordCurve(result);
                return result.Outcome is CpuLiveCurveOutcome.Verified or CpuLiveCurveOutcome.Unchanged;
            }
            async Task<bool> ExecutePowerAsync()
            {
                if (!requested.HasFlag(CpuTuningFeatures.PowerLimit)) { return true; }
                if (!lease.Allows(required)) { return false; }
                var result = await livePower!.ExecutePreparedAsync(powerOperation!, CheckCurveAsync, token, curveOperation?.PeerState).ConfigureAwait(false);
                RecordPower(result);
                return result.Outcome is CpuLivePowerOutcome.Verified or CpuLivePowerOutcome.Unchanged;
            }
        }
        finally
        {
            curveOperation?.Close(); powerOperation?.Close();
            try { lease.Dispose(); }
            finally { liveAdmission.Value = null; liveGate.Release(); }
        }

        void RecordCurve(CpuLiveCurveTransactionResult result)
        {
            RetainUncertainty(result); curve = result.Observation; uncertain |= result.Outcome == CpuLiveCurveOutcome.Uncertain;
            confirmedPeerLoss |= result.Reason is CpuLiveCurveBlockReason.StateChanged or CpuLiveCurveBlockReason.ContextMismatch;
        }
        void RecordPower(CpuLivePowerTransactionResult result)
        {
            RetainUncertainty(result); power = result.Observation; uncertain |= result.Outcome == CpuLivePowerOutcome.Uncertain;
            confirmedPeerLoss |= result.Reason is CpuLivePowerBlockReason.StateChanged or CpuLivePowerBlockReason.ContextMismatch;
        }
        async Task<ControlOperationResult> FinishAsync(bool completed)
        {
            // Gates are released before these journal-only CAS operations. A
            // confirmed peer loss must survive restart even if that peer never
            // needed a setter or has already been restored in this operation.
            if (confirmedPeerLoss)
            {
                if (curveOperation is not null)
                { RetainUncertainty(await liveCurve!.RejectPreparedAsync(curveOperation).ConfigureAwait(false)); }
                if (powerOperation is not null)
                { RetainUncertainty(await livePower!.RejectPreparedAsync(powerOperation).ConfigureAwait(false)); }
                completed = false;
            }
            var retained = await ReadLiveOwnershipAsync(CancellationToken.None).ConfigureAwait(false);
            uncertain &= liveUncertainty != CpuTuningFeatures.None;
            completed &= !retained.Blocked && !token.IsCancellationRequested;
            var recovery = retained.Features == CpuTuningFeatures.None ? CpuTuningRecoveryState.None
                : completed ? CpuTuningRecoveryState.RestoreReady : CpuTuningRecoveryState.Blocked;
            return new(completed ? ControlOperationStatus.Stored : uncertain ? ControlOperationStatus.Failed : ControlOperationStatus.Blocked,
                completed ? restoring ? "The original live CPU settings were restored and verified." : "The requested live CPU settings were verified."
                    : uncertain ? "Live CPU submission or readback is uncertain. Further live commands are blocked."
                    : confirmedPeerLoss ? "Confirmed live CPU state changed outside this operation. Ownership is blocked for both participating families."
                    : "The live CPU operation stopped before completion; verified changes and recovery data were retained.")
            {
                CpuTuning = LiveStatus(curve, power, recovery, retained.Features,
                    completed && liveCurve is not null && curve is not null && lease.CanPresentCurveWrite()),
            };
        }
    }

    private static bool CompatibleContexts(CpuLiveCurveContext curve, CpuLivePowerContext power) =>
        CpuHardwareIdentity.Same(curve.Identity, power.Identity) && curve.Backend == power.Backend && curve.BootSequence == power.BootSequence
        && curve.RuntimeLifetime == power.RuntimeLifetime
        && curve.EnabledCoreMask == power.EnabledCoreMask && curve.PmVersion == power.PmVersion && curve.PmTableLength == power.PmTableLength;

    private async Task<(bool Blocked, CpuTuningFeatures Features)> ReadLiveOwnershipAsync(CancellationToken token)
    {
        if (liveCurve is not null && await liveCurve.TrySettlePendingSubmissionAsync(token).ConfigureAwait(false))
        { liveUncertainty &= ~CpuTuningFeatures.CurveOptimizer; }
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
        CpuTuningRecoveryState recovery, CpuTuningFeatures features, bool curveWriteEligible = false)
    {
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
        // Publish only the independently observed families. Raw fast/slow limits
        // do not establish effective watts or any CO values.
        CpuLiveState? live = curve is null && power is null ? null : new(power is null ? null
            : new(null, power.Fast.Milliwatts, power.Slow.Milliwatts),
            curve?.CoreOffsets.Select(core => new CpuCoreCurveOptimizerValue(core.PhysicalCoreId, core.Offset)).ToArray() ?? []);
        return new(null, live, unavailable, unavailable with
        {
            Access = curveWriteEligible ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly,
            Availability = curve is not null || power is not null ? CpuTuningBackendAvailability.Available : CpuTuningBackendAvailability.Unavailable,
        },
            CpuTuningPendingBoot.Unknown, features != CpuTuningFeatures.None)
        { LiveRecovery = new(recovery, features) };
    }

    // Runtime admission expires with its initiating call. AsyncLocal captures the
    // exact lease for late continuations; a later request cannot renew it. Native
    // backends still own the actual worker and repeat their independent proofs.
    private sealed class LiveAdmission(Func<CpuTuningFeatures, bool> admit, CancellationToken token,
        CpuLiveRuntimeLifetime lifetime, CpuSavedAdmissionDiagnostic? diagnostic = null,
        Func<CpuTuningFeatures, CancellationToken, bool>? observationAdmission = null,
        Func<CpuTuningFeatures, CancellationToken, Func<bool>, CpuLiveGetterAdmission>? getterAdmission = null) : IDisposable
    {
        internal CpuLiveRuntimeLifetime Lifetime { get; } = lifetime;
        internal CpuSavedAdmissionDiagnostic? Diagnostic { get; } = diagnostic;
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        private int disposed;
        internal CancellationToken Token => cancellation.Token;
        private int revoked;
        private CpuTuningFeatures requiredFeatures;
        internal void Require(CpuTuningFeatures features) => requiredFeatures |= features;
        internal CpuLiveGetterAdmission? BeginGetter(CancellationToken operationToken)
        {
            bool Current() => Volatile.Read(ref disposed) == 0 && Volatile.Read(ref revoked) == 0
                && !token.IsCancellationRequested && !operationToken.IsCancellationRequested;
            return !Current() ? null : getterAdmission?.Invoke(requiredFeatures, operationToken, Current);
        }
        // Presentation is calculated only after query/transaction work is complete.
        // Feature-specific denial must not revoke a valid read-only query lease;
        // this flag grants no command, range, restoration, or native authority.
        internal bool CanPresentCurveWrite()
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref revoked) != 0 || token.IsCancellationRequested)
            { return false; }
            bool admitted;
            try { admitted = admit(CpuTuningFeatures.CurveOptimizer | requiredFeatures); }
            catch { return false; }
            return admitted && Volatile.Read(ref disposed) == 0 && Volatile.Read(ref revoked) == 0
                && !token.IsCancellationRequested;
        }
        internal bool Allows(CpuTuningFeatures feature, CancellationToken operationToken = default)
        {
            // Only observation has operation-scoped admission. Mutation and its
            // independent restoration callbacks keep their existing token path.
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref revoked) != 0 || token.IsCancellationRequested)
            { return false; }
            CancellationToken admissionToken = operationToken.CanBeCanceled ? operationToken : token;
            if (observationAdmission is not null && admissionToken.IsCancellationRequested) { return false; }
            bool admitted;
            try
            {
                admitted = observationAdmission is null ? admit(feature | requiredFeatures)
                    : observationAdmission(feature | requiredFeatures, admissionToken);
            }
            catch { Interlocked.Exchange(ref revoked, 1); return false; }
            if (admitted && Volatile.Read(ref disposed) == 0 && Volatile.Read(ref revoked) == 0
                && !token.IsCancellationRequested
                && (observationAdmission is null || !admissionToken.IsCancellationRequested)) { return true; }
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
        public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token) => ObserveAsync(token, null);
        public async Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.None, token) != true)
            { diagnostic?.Check(false, "runtime curve observation lease before query"); return new(null, null); }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            var result = await backend.ObserveAsync(cancellation.Token, diagnostic).ConfigureAwait(false);
            if (lease.Allows(CpuTuningFeatures.None, token)) { return result; }
            diagnostic?.Check(false, "runtime curve observation lease after query"); return new(null, null);
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
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token) =>
            await SendAsync(context, command, purpose, expectedBefore, null, token).ConfigureAwait(false);
        public async Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CpuLivePowerPeerState? peer, CancellationToken token)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.CurveOptimizer) != true) { return CpuLiveCurveSubmission.NotSubmitted; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            // Await the actual terminal task: the transaction bounds its wait.
            // Keep this linked CTS alive until native cleanup has finished.
            var result = await backend.SendAsync(context, command, purpose, expectedBefore, peer, cancellation.Token).ConfigureAwait(false);
            return result == CpuLiveCurveSubmission.NotSubmitted || lease.Allows(CpuTuningFeatures.CurveOptimizer)
                ? result : CpuLiveCurveSubmission.Unknown;
        }
    }
    private sealed class RuntimePowerBackend(ICpuLivePowerBackend backend, AsyncLocal<LiveAdmission?> current) : ICpuLivePowerBackend
    {
        public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token) => ObserveAsync(token, null);
        public async Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic)
        {
            var lease = current.Value;
            if (lease?.Allows(CpuTuningFeatures.None, token) != true)
            { diagnostic?.Check(false, "runtime power observation lease before query"); return new(null, null, null, null, null); }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
            var result = await backend.ObserveAsync(cancellation.Token, diagnostic).ConfigureAwait(false);
            if (lease.Allows(CpuTuningFeatures.None, token)) { return result; }
            diagnostic?.Check(false, "runtime power observation lease after query"); return new(null, null, null, null, null);
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
        if (!CpuHardwareIdentity.IsRz090528(identity))
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
