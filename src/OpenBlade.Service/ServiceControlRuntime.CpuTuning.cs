using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    private readonly object cpuPowerClassificationSync = new();
    private Task<PowerSource>? cpuPowerClassification;

    // Native admission callbacks are synchronous. Run the fresh classifier off
    // their context, retaining its actual task until terminal if a caller times
    // out. A late classification cannot authorize a write or overlap a retry.
    private bool CpuFreshFullPower(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic = null)
    {
        token.ThrowIfCancellationRequested();
        using var stage = diagnostic?.BeginStage("fresh power classification wait");
        Task<PowerSource> worker;
        CancellationToken workerToken;
        lock (cpuPowerClassificationSync)
        {
            if (cpuPowerClassification is { IsCompleted: false })
            {
                diagnostic?.Check(false, "runtime fresh power source (prior classification still running)");
                return false;
            }
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            workerToken = deadline.Token;
            worker = Task.Run(async () =>
            {
                try
                {
                    workerToken.ThrowIfCancellationRequested();
                    PowerSource result = await ResolveCurrentPowerSourceAsync(workerToken,
                        allowCachedExternalClassification: false, cpuDiagnostic: diagnostic).ConfigureAwait(false);
                    workerToken.ThrowIfCancellationRequested();
                    return result;
                }
                finally { deadline.Dispose(); }
            }, CancellationToken.None);
            cpuPowerClassification = worker;
        }
        _ = worker.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            PowerSource result = worker.WaitAsync(workerToken).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            bool fullPower = result == PowerSource.PluggedIn;
            if (!fullPower)
            {
                diagnostic?.Check(false, $"runtime fresh power source (resolved {result}; required PluggedIn)");
            }
            return fullPower;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            diagnostic?.Check(false, workerToken.IsCancellationRequested
                ? "runtime fresh power source (10-second classification deadline elapsed; caller not canceled)"
                : "runtime fresh power source (classification canceled independently; caller and deadline not canceled)");
            return false;
        }
    }

    public async Task<ControlOperationResult> ObserveCpuTuningAsync(bool useInstalledBackend,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var diagnostic = new CpuSavedAdmissionDiagnostic();
            ServiceStatusSnapshot current = GetStatus();
            if (!diagnostic.Check(stillControlsSession(), "runtime controlling session")
                || !diagnostic.Check(!lowPowerGate.IsActive, "runtime active power lifecycle")
                || !diagnostic.Check(current.DeviceVerified, "runtime verified device status")
                || !diagnostic.Check(current.Capabilities?.CpuPowerLimit is not null, "runtime supported CPU capability")
                || !diagnostic.Check(deviceProvider.TryGetRecentlyVerifiedReadOnlyDevice(out _), "runtime recently verified device"))
            {
                return new(ControlOperationStatus.Blocked,
                    $"CPU observation requires the active session and verified supported laptop. Admission failed at: {diagnostic.FirstFailure}.");
            }

            long liveGeneration = lowPowerGate.Generation;
            long livePowerSourceGeneration = powerSourceGeneration;
            // Optional write-presentation refusal must not mask a later outer
            // session rejection. Native workers retain only this inner record.
            var liveDiagnostic = new CpuSavedAdmissionDiagnostic();
            CpuTuningStatus observation = await cpuTuningController.ObserveAsync(useInstalledBackend, token)
                .ConfigureAwait(false);
            if (useInstalledBackend && cpuTuningController.HasLiveBackend)
            {
                CpuTuningStatus live = await cpuTuningController.ObserveLiveAsync(
                    (feature, operationToken) => CpuLiveRuntimeAdmission(feature, stillControlsSession, liveGeneration, livePowerSourceGeneration, operationToken, liveDiagnostic),
                    token, liveGeneration, livePowerSourceGeneration, liveDiagnostic,
                    (features, operationToken, lifetime) => CreateCpuLiveGetterAdmission(features, stillControlsSession,
                        liveGeneration, livePowerSourceGeneration, operationToken, lifetime, liveDiagnostic)).ConfigureAwait(false);
                observation = MergeLiveCpuStatus(observation, live);
            }
            if (!diagnostic.Check(stillControlsSession(), "runtime controlling session")
                || !diagnostic.Check(!lowPowerGate.IsActive, "runtime active power lifecycle")
                || useInstalledBackend && cpuTuningController.HasLiveBackend
                    && !CpuLiveRuntimeAdmission(CpuTuningFeatures.None, stillControlsSession, liveGeneration, livePowerSourceGeneration, token, diagnostic))
            {
                return new(ControlOperationStatus.Blocked,
                    "The CPU observation session changed."
                    + (liveDiagnostic.LiveObservationReason is { } innerReason ? $" Inner live observation: {innerReason}." : string.Empty)
                    + (liveDiagnostic.FirstFailure is { } innerFailure ? $" Inner first rejection: {innerFailure}." : string.Empty)
                    + $" Admission failed at: {diagnostic.FirstFailure}.");
            }
            PublishStatusUpdate(status => status with { CpuTuning = observation });
            return new(ControlOperationStatus.Stored,
                (observation.LiveState is null ? "CPU configuration was inspected. Live measurements remain unavailable."
                    : "Saved configuration and live CPU observations were inspected.")
                    + (liveDiagnostic.LiveObservationReason is { } reason ? $" Live observation: {reason}." : string.Empty)
                    + (liveDiagnostic.FirstFailure is { } failure ? $" First rejection: {failure}." : string.Empty))
            {
                CpuTuning = observation,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return new(ControlOperationStatus.Failed,
                "CPU observation is unavailable; the previous observation was retained.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(ControlOperationStatus.Failed,
                "CPU observation timed out; the previous observation was retained.");
        }
        finally { operationGate.Release(); }
    }

    public async Task<ControlOperationResult> SetCpuTuningAsync(CpuTuningTarget target, CpuTuningSettings settings,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(target) || settings is not { IsWellFormed: true, IsConfigured: true })
        {
            return new(ControlOperationStatus.Failed, "CPU tuning intent is invalid.");
        }
        return await MutateCpuTuningAsync(target, settings, restoring: false,
            stillControlsSession, token).ConfigureAwait(false);
    }

    public async Task<ControlOperationResult> RestoreCpuTuningAsync(CpuTuningTarget target,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(target))
        { return new(ControlOperationStatus.Failed, "CPU tuning target is invalid."); }
        return await MutateCpuTuningAsync(target, null, restoring: true,
            stillControlsSession, token).ConfigureAwait(false);
    }

    private async Task<ControlOperationResult> MutateCpuTuningAsync(CpuTuningTarget target,
        CpuTuningSettings? settings, bool restoring, Func<bool> stillControlsSession, CancellationToken token)
    {
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceStatusSnapshot current = GetStatus();
            if (target == CpuTuningTarget.LiveNow)
            {
                if (!cpuTuningController.HasLiveBackend || !CpuMutationSafetyAvailable(current, stillControlsSession)
                    || !CpuFeaturesAdmitted(current.Capabilities, settings, restoring))
                {
                    return new(ControlOperationStatus.Blocked, "Live CPU tuning requires an admitted backend and fresh full-power session.")
                    { CpuTuning = current.CpuTuning };
                }
                long generation = lowPowerGate.Generation;
                long sourceGeneration = powerSourceGeneration;
                ControlOperationResult live = await cpuTuningController.MutateLiveAsync(settings, restoring,
                    feature => CpuLiveRuntimeAdmission(feature, stillControlsSession, generation, sourceGeneration, token),
                    token, generation, sourceGeneration,
                    (features, operationToken, lifetime) => CreateCpuLiveGetterAdmission(features, stillControlsSession,
                        generation, sourceGeneration, operationToken, lifetime,
                        cpuTuningController.CaptureLiveObservationDiagnostic())).ConfigureAwait(false);
                if (live.CpuTuning is { } liveStatus)
                {
                    var merged = MergeLiveCpuStatus(GetStatus().CpuTuning, liveStatus);
                    PublishStatusUpdate(status => status with { CpuTuning = merged });
                    live = live with { CpuTuning = merged };
                }
                return live;
            }
            if (!cpuTuningController.HasSavedBackend
                || !CpuMutationSafetyAvailable(current, stillControlsSession)
                || !CpuSavedFeaturesEligible(current.Capabilities, settings, restoring))
            {
                return new(ControlOperationStatus.Blocked,
                    "CPU tuning requires admitted commands, exact restoration, and the active session on full power.")
                { CpuTuning = current.CpuTuning };
            }
            long savedGeneration = lowPowerGate.Generation;
            var diagnostic = new CpuSavedAdmissionDiagnostic();
            bool FreshAdmission(DeviceIdentity identity, CpuSavedTuningCommand command)
            {
                if (!diagnostic.Check(lowPowerGate.Generation == savedGeneration, "runtime power generation before probes")
                    || !diagnostic.Check(CpuSavedTuningPlan.IsExactIdentity(identity), "runtime command identity")
                    || !CpuMutationSafetyAvailable(GetStatus(), stillControlsSession, diagnostic)
                    || !diagnostic.Check(CpuSavedFeaturesEligible(GetStatus().Capabilities, settings, restoring), "runtime requested saved features before probes")
                    || !diagnostic.Check(CpuSavedCommandFeatureEligible(GetStatus().Capabilities, command), "runtime saved command feature before probes")) { return false; }
                ControllerPresenceObservation presence = controllerPresenceMonitor.Observe();
                return diagnostic.Check(presence.DetectionWarning is null, "runtime controller detection")
                    && diagnostic.Check(presence.PotentialControllers.Count == 0, "runtime controller absence")
                    && diagnostic.Check(CpuFreshFullPower(token, diagnostic), "runtime fresh power source")
                    && diagnostic.Check(deviceProvider.TryGetVerifiedDevice(out _), "runtime fresh verified device")
                    && diagnostic.Check(lowPowerGate.Generation == savedGeneration, "runtime power generation after probes")
                    && CpuMutationSafetyAvailable(GetStatus(), stillControlsSession, diagnostic)
                    && diagnostic.Check(CpuSavedFeaturesEligible(GetStatus().Capabilities, settings, restoring), "runtime requested saved features after probes")
                    && diagnostic.Check(CpuSavedCommandFeatureEligible(GetStatus().Capabilities, command), "runtime saved command feature after probes");
            }
            ControlOperationResult result = restoring
                ? await cpuTuningController.RestoreSavedAsync(FreshAdmission, token, diagnostic).ConfigureAwait(false)
                : await cpuTuningController.ApplySavedAsync(settings!, FreshAdmission, token, diagnostic).ConfigureAwait(false);
            if (result.CpuTuning is { } observed)
            {
                CpuTuningStatus? previous = GetStatus().CpuTuning;
                observed = observed with
                {
                    SavedConfiguration = observed.SavedConfiguration ?? previous?.SavedConfiguration,
                    LiveState = previous?.LiveState,
                    Live = previous?.Live ?? observed.Live,
                    LiveRecovery = previous?.LiveRecovery,
                    RestorePending = observed.RestorePending || HasCpuRecovery(previous?.LiveRecovery),
                };
                PublishStatusUpdate(status => status with { CpuTuning = observed });
                result = result with { CpuTuning = observed };
            }
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(ControlOperationStatus.Failed, "CPU readback timed out. No further CPU command was submitted.")
            { CpuTuning = GetStatus().CpuTuning };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return new(ControlOperationStatus.Failed, "CPU tuning is unavailable; recovery data was retained.")
            { CpuTuning = GetStatus().CpuTuning };
        }
        finally { operationGate.Release(); }
    }

    private CpuLiveGetterAdmission CreateCpuLiveGetterAdmission(CpuTuningFeatures features,
        Func<bool> stillControlsSession, long generation, long sourceGeneration, CancellationToken token,
        Func<bool> lifetime, CpuSavedAdmissionDiagnostic? diagnostic = null) =>
        new(token, lifetime, proof => CpuLiveRuntimeAdmission(features, stillControlsSession,
            generation, sourceGeneration, token, diagnostic, proof),
            continuity: proof => CpuLiveRuntimeAdmission(features, stillControlsSession,
                generation, sourceGeneration, token, diagnostic, proof, continuity: true), diagnostic: diagnostic);

    private bool CpuLiveRuntimeAdmission(CpuTuningFeatures features, Func<bool> stillControlsSession, long generation,
        long sourceGeneration, CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic = null,
        CpuLiveGetterAdmission? getter = null, bool continuity = false)
    {
        bool Check(bool allowed, string stage) => diagnostic?.Check(allowed, stage) ?? allowed;
        ServiceStatusSnapshot current = GetStatus();
        if (!Check(lowPowerGate.Generation == generation, "runtime power generation before probes")
            || !Check(powerSourceGeneration == sourceGeneration, "runtime power source generation before probes")
            || !CpuMutationSafetyAvailable(current, stillControlsSession, diagnostic)
            || (features & CpuTuningFeatures.CurveOptimizer) != 0
                && !Check(current.Capabilities?.CpuVoltageOptimizer?.Access == DeviceFeatureAccess.ReadWrite, "runtime curve write capability")
            || (features & CpuTuningFeatures.PowerLimit) != 0
                && !Check(current.Capabilities?.CpuPowerLimit?.Access == DeviceFeatureAccess.ReadWrite, "runtime power write capability")) { return false; }
        bool ControllersAdmitted()
        {
            // The native getter checks fresh bounded process/SCM activity against
            // its fully verified inventory. Other operations retain full discovery.
            if (continuity) { return true; }
            ControllerPresenceObservation presence = controllerPresenceMonitor.Observe();
            return Check(presence.DetectionWarning is null, "runtime fresh controller detection")
                && Check(presence.PotentialControllers.Count == 0, "runtime fresh controller absence");
        }
        bool DeviceAdmitted()
        {
            if (continuity && deviceProvider is WindowsVerifiedBladeDeviceProvider windows)
            { return getter?.Device is { } retained && windows.IsCpuGetterDeviceCurrent(retained); }
            return deviceProvider.TryGetVerifiedDevice(out var device)
                && (getter is null || getter.BindDevice(device));
        }
        return ControllersAdmitted()
            && Check(getter is null ? CpuFreshFullPower(token, diagnostic)
                : getter.FullPower(() => CpuFreshFullPower(token, diagnostic), powerStateProvider.CapturePowerSource), "runtime fresh power source")
            && Check(DeviceAdmitted(), "runtime fresh verified device")
            && Check(lowPowerGate.Generation == generation, "runtime power generation after probes")
            && Check(powerSourceGeneration == sourceGeneration, "runtime power source generation after probes")
            && CpuMutationSafetyAvailable(GetStatus(), stillControlsSession, diagnostic);
    }

    private static CpuTuningStatus MergeLiveCpuStatus(CpuTuningStatus? previous, CpuTuningStatus live) => live with
    {
        SavedConfiguration = previous?.SavedConfiguration,
        Firmware = previous?.Firmware ?? live.Firmware,
        FirmwareRecovery = previous?.FirmwareRecovery,
        PendingBoot = previous?.PendingBoot ?? CpuTuningPendingBoot.Unknown,
        RestorePending = live.RestorePending || HasCpuRecovery(previous?.FirmwareRecovery),
    };
    private static bool HasCpuRecovery(CpuTuningRecoveryStatus? recovery) =>
        recovery is { State: CpuTuningRecoveryState.RestoreReady or CpuTuningRecoveryState.Blocked, Features: not CpuTuningFeatures.None };

    private bool CpuMutationSafetyAvailable(ServiceStatusSnapshot current, Func<bool> stillControlsSession,
        CpuSavedAdmissionDiagnostic? diagnostic = null)
    {
        bool Check(bool allowed, string stage) => diagnostic?.Check(allowed, stage) ?? allowed;
        return Check(stillControlsSession(), "runtime controlling session")
            && Check(!lowPowerGate.IsActive, "runtime active power lifecycle")
            && Check(!thermalConvergenceShutdown, "runtime thermal shutdown")
            && Check(current.DeviceVerified, "runtime verified device status")
            && Check(!current.WritesBlocked, "runtime write safety status")
            && Check(current.ControlAccess == SessionControlAccess.Active, "runtime control access status")
            && Check(current.ActivePowerSource == PowerSource.PluggedIn, "runtime power source status")
            && Check(current.ControllerDetectionWarning is null, "runtime controller detection status")
            && Check(current.PotentialControllers.Count == 0, "runtime controller absence status")
            && Check(current.UnconfirmedWrites.Count == 0, "runtime unconfirmed write status")
            && Check(!pendingWriteConfirmations.HasPending, "runtime pending write confirmations")
            && Check(current.ConfirmedState?.HyperBoostActive != true, "runtime HyperBoost state")
            && Check(!current.HyperBoostRestorePending, "runtime HyperBoost restoration status")
            && Check(!hyperBoostJournalSafetyBlock, "runtime HyperBoost journal safety")
            && Check(!hyperBoostRestorePending, "runtime HyperBoost restoration");
    }

    // The durable reverse plan determines restoration's required features.
    // Every reverse command is admitted before any submission and again at send
    // time; unrelated advertised features confer no authority.
    private static bool CpuFeaturesAdmitted(DeviceCapabilities? capabilities, CpuTuningSettings? settings, bool restoring) =>
        capabilities is not null
        && (restoring || settings is not null
                && (settings.PowerLimitWatts is null || capabilities.CpuPowerLimit is { Access: DeviceFeatureAccess.ReadWrite })
                && (settings.CurveOptimizer is null || capabilities.CpuVoltageOptimizer is { Access: DeviceFeatureAccess.ReadWrite }));

    // Discovery describes the saved feature; the backend independently admits
    // every exact forward and reverse command. It remains production-disabled.
    private static bool CpuSavedFeatureAccessEligible(DeviceFeatureAccess access) =>
        access is DeviceFeatureAccess.ObservedOnly or DeviceFeatureAccess.ReadWrite;

    private static bool CpuSavedPowerEligible(DeviceCapabilities? capabilities) =>
        capabilities?.CpuPowerLimit is { IsWellFormed: true } power && CpuSavedFeatureAccessEligible(power.Access);

    private static bool CpuSavedCurveEligible(DeviceCapabilities? capabilities) =>
        capabilities?.CpuVoltageOptimizer is { IsWellFormed: true } curve && CpuSavedFeatureAccessEligible(curve.Access);

    private static bool CpuSavedFeaturesEligible(DeviceCapabilities? capabilities, CpuTuningSettings? settings, bool restoring) =>
        capabilities is not null && (restoring || settings is { IsWellFormed: true, IsConfigured: true }
            && (settings.PowerLimitWatts is null || CpuSavedPowerEligible(capabilities)
                && capabilities.CpuPowerLimit!.Watts.Supports(settings.PowerLimitWatts.Value))
            && (settings.CurveOptimizer is null || CpuSavedCurveEligible(capabilities)
                && (settings.CurveOptimizer.Enabled
                    ? capabilities.CpuVoltageOptimizer!.Value.Supports(settings.CurveOptimizer.Value)
                    : capabilities.CpuVoltageOptimizer!.SupportsEnableDisable)));

    // Originals are admitted by the exact restoration plan, not the forward
    // editor range. Zero CO still requires explicit disable support; PPT reset
    // is a distinct typed reverse command rather than an editable zero watts.
    private static bool CpuSavedCommandFeatureEligible(DeviceCapabilities? capabilities, CpuSavedTuningCommand command) =>
        command.IsWellFormed && (command.Kind switch
        {
            CpuSavedTuningCommandKind.CurveOptimizer => CpuSavedCurveEligible(capabilities)
                && (command.Value != 0 || capabilities!.CpuVoltageOptimizer!.SupportsEnableDisable),
            CpuSavedTuningCommandKind.PowerLimit or CpuSavedTuningCommandKind.ResetPowerLimit => CpuSavedPowerEligible(capabilities),
            _ => false,
        });
}
