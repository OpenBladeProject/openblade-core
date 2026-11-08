using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    private readonly FanStopController fanStopController;
    private int fanStopShutdownStarted;
    private long synchronizedFanStopGeneration;
    private FanStopState recordedFanStopState = FanStopState.Inactive;
    private string? recordedFanStopMessage;
    internal const string GuardianHardwareBlockedMessage = "Ordinary hardware cleanup is blocked by the active fan guardian delegation.";
    private long guardianNotificationVersion;
    private int guardianNotificationPump;
    private int guardianNotificationsStopped;
    private int guardianHardwareDelegated;
    private bool guardianDeferredPowerTransition;

    private async Task<IDisposable> AcquireGuardianHardwareLeaseAsync(CancellationToken token)
    {
        IDisposable lease = await deviceProvider.AcquireFanStopHardwareLeaseAsync(token).ConfigureAwait(false);
        Volatile.Write(ref guardianHardwareDelegated, 1);
        return new ActionDisposable(() =>
        {
            Volatile.Write(ref guardianHardwareDelegated, 0);
            lease.Dispose();
        });
    }

    private void ObserveGuardianPowerSource()
    {
        if (fanStopController.Guardian is not { HasOperation: true } guardian) { return; }
        FanStopGuardianSnapshot snapshot = guardian.Snapshot;
        if (snapshot.Unresolved || snapshot.Failure is not null
            || snapshot.ObservedPowerSource is not { } source || source == PowerSource.ExternalUnknown)
        { return; }
        ServiceStatusSnapshot current = GetStatus();
        if (source == current.ActivePowerSource) { return; }
        if (ChangePowerSourceCore(source)) { guardianDeferredPowerTransition = true; }
        PublishIfChanged(current with
        {
            ActivePowerSource = source,
            DesiredState = ResolveAutomationDesired(current.ConfirmedState ?? UnknownConfirmedState),
        });
    }

    // Caller owns operationGate. A known decrease stores destination intent;
    // ordinary profile writes wait for the same owner's verified Auto/release.
    private static int FanStopPowerStrength(PowerSource value) => value switch
    { PowerSource.PluggedIn => 3, PowerSource.UsbC => 2, PowerSource.Battery => 1, _ => 0 };

    private void PauseAutomaticFanStopForPowerIncrease(PowerSource source)
    {
        int previousStrength = FanStopPowerStrength(GetStatus().ActivePowerSource);
        if (previousStrength > 0 && FanStopPowerStrength(source) > previousStrength)
        { PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason.PowerChanged); }
    }

    private async Task<bool> HandleGuardianPowerBoundaryAsync(PowerSource source, CancellationToken token)
    {
        PauseAutomaticFanStopForPowerIncrease(source);
        if (fanStopController.Guardian is null || !fanStopController.BlocksOrdinaryThermalWrites) { return false; }
        await fanStopController.RefreshAsync(true, () => false, token).ConfigureAwait(false);
        if (!fanStopController.BlocksOrdinaryThermalWrites) { return false; }
        ServiceStatusSnapshot current = GetStatus();
        if (source == PowerSource.ExternalUnknown && !fanStopController.RequiresRecovery)
        {
            // Windows knows an external supply exists, but only the sole owner
            // may issue the reviewed adapter query during delegation. It chooses
            // higher-power Auto or verifies retained Stop on a decrease.
            guardianDeferredPowerTransition = true;
            PublishFanStopOutcome();
            return true;
        }
        if (!fanStopController.RequiresRecovery && FanStopPowerStrength(source) != 0
            && FanStopPowerStrength(current.ActivePowerSource) != 0
            && FanStopPowerStrength(source) <= FanStopPowerStrength(current.ActivePowerSource))
        {
            if (source != current.ActivePowerSource)
            {
                ChangePowerSourceCore(source);
                guardianDeferredPowerTransition = true;
            }
            PublishIfChanged(current with
            {
                ActivePowerSource = source,
                DesiredState = ResolveAutomationDesired(current.ConfirmedState ?? UnknownConfirmedState),
                Message = "The guardian retains Stop while verifying its owned state on the lower-power source; destination profile application is deferred.",
            });
            PublishFanStopOutcome();
            return true;
        }
        // A known increase requires same-run Auto. Native release must precede
        // the ordinary destination profile commands.
        ControlOperationResult? release = await ReleaseFanStopCoreAsync(token).ConfigureAwait(false);
        return release?.Status != ControlOperationStatus.Applied;
    }

    private void NotifyGuardianSnapshotChanged()
    {
        if (Volatile.Read(ref guardianNotificationsStopped) != 0) { return; }
        Interlocked.Increment(ref guardianNotificationVersion);
        if (Interlocked.CompareExchange(ref guardianNotificationPump, 1, 0) == 0)
        {
            _ = Task.Run(PublishGuardianSnapshotsAsync);
        }
    }

    private async Task PublishGuardianSnapshotsAsync()
    {
        long observed = 0;
        try
        {
            do
            {
                observed = Volatile.Read(ref guardianNotificationVersion);
                // Publish/reconcile the existing owner and any deferred profile
                // handback. Only periodic maintenance may sample temperatures
                // for automatic entry or start Stop.
                if (Volatile.Read(ref guardianNotificationsStopped) == 0 && Volatile.Read(ref status) is not null)
                { await MaintainFanStopAsync(CancellationToken.None, allowAutomaticEntry: false).ConfigureAwait(false); }
            }
            while (observed != Volatile.Read(ref guardianNotificationVersion));
        }
        catch (Exception error)
        {
            history.RecordSummary(ControlOperationStatus.Blocked,
                $"Guardian status publication failed: {error.Message}");
        }
        finally
        {
            Volatile.Write(ref guardianNotificationPump, 0);
            if (observed != Volatile.Read(ref guardianNotificationVersion)) { NotifyGuardianSnapshotChanged(); }
        }
    }

    // One immutable accepted status plus immediate volatile termination flags.
    // This is the heartbeat's only service input: no backend observation,
    // controller mutation, device/OS query or operation-gate acquisition.
    internal FanStopAdmission ReadFanStopContinuationAdmission()
    {
        ServiceStatusSnapshot? current = Volatile.Read(ref status);
        if (current is null)
        { return new(false, false, false, false, false, false, false, false, false, false); }
        bool session = Volatile.Read(ref pendingSessionAdmissions) == 0
            && !Volatile.Read(ref activeUserActivationIncomplete)
            && current.ControlAccess == SessionControlAccess.Active;
        bool active = ConfigurationIsAdmitted && session
            && Volatile.Read(ref guardianHardwareDelegated) != 0
            && Volatile.Read(ref fanStopShutdownStarted) == 0
            && Volatile.Read(ref guardianNotificationsStopped) == 0
            && !Volatile.Read(ref thermalConvergenceShutdown) && !lowPowerGate.IsActive
            && current.FanStopStatus.State != FanStopState.RecoveryRequired;
        return new(current.DeviceVerified && current.Capabilities?.HasFanStopTrialEvidence == true,
            !FanControllerAdmissionUnsafe(current), PowerKnown: true, session, active,
            current.ConfirmedState?.Performance == PerformancePreset.Balanced,
            PowerInScope: true, TemperatureSafe: true,
            OwnershipConfirmed: Volatile.Read(ref guardianHardwareDelegated) != 0,
            TransactionResolved: !current.WritesBlocked && current.UnconfirmedWrites.Count == 0
                && current.FanStopStatus.State != FanStopState.RecoveryRequired);
        // Power and thermal freshness remain the sole native owner's job. A
        // cached service classification is deliberately not sent as a fresh read.
    }

    // Invoked under operationGate by the explicit admission callback. HID is used
    // only before delegation. The owner performs its own fresh native admission
    // during delegation; status publication cannot issue A0 or HID commands
    // while that owner holds the hardware lease. After release it may refresh
    // HID admission for presentation, without starting a new Stop.
    internal async Task<FanStopAdmission> ReadFanStopServiceAdmissionAsync(CancellationToken token)
    {
        ServiceStatusSnapshot current = GetStatus();
        bool delegated = fanStopController.Guardian?.BlocksOrdinaryHardware == true;
        bool profile = current.ConfirmedState?.Performance == PerformancePreset.Balanced;
        bool exact = current.DeviceVerified && current.Capabilities?.HasFanStopTrialEvidence == true;
        if (!delegated && !fanStopController.RequiresRecovery && exact
            && deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
        {
            ConfirmedDeviceState observed = await device.ReadPerformanceFanStateAsync(token).ConfigureAwait(false);
            profile = observed.Performance == PerformancePreset.Balanced;
        }
        PowerSource source = delegated ? powerStateProvider.CapturePowerSource()
            : await ResolveCurrentPowerSourceAsync(token).ConfigureAwait(false);
        return new(exact, !FanControllerAdmissionUnsafe(current), delegated || source != PowerSource.ExternalUnknown,
            activeUserStillControlsSession?.Invoke() != false, delegated ? FanStopOwnedContinuationAdmitted() : FanStopEntryAdmitted(), profile,
            delegated || source == PowerSource.UsbC, TemperatureSafe: true,
            OwnershipConfirmed: delegated && !fanStopController.RequiresRecovery,
            TransactionResolved: !fanStopController.RequiresRecovery
                && !pendingWriteConfirmations.HasPending && !laptopControlExecutor.RequiresReconciliation
                && !hyperBoostJournalSafetyBlock && !current.WritesBlocked)
        { ObservedPowerSource = source };
    }

    public async Task<ControlOperationResult> SetFanStopModeAsync(
        FanStopMode mode, Func<bool> stillControlsSession, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (mode == FanStopMode.Automatic && stillControlsSession())
        { PauseAutomaticFanStop(FanStopAutomationPauseReason.ManualAuto); }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceStatusSnapshot current = GetStatus();
            if (!stillControlsSession())
            {
                return new(ControlOperationStatus.Blocked, "The active Windows session changed before fan-stop dispatch.");
            }
            // Stop is transient control intent. Do not modify the saved fan profile
            // or use the legacy HID FanMode.Stop path.
            ResetAutomaticFanStopTimer();
            if (mode == FanStopMode.Stop && !fanStopController.BlocksOrdinaryThermalWrites)
            { fanStopController.Guardian?.ConfigureSettings(preferences.FanStopSettings); }
            ControlOperationResult result = await fanStopController.SetModeAsync(
                mode, current.Capabilities?.HasFanStopTrialEvidence == true,
                ResolveCurrentFanIntent(),
                FanStopCommandAdmitted, cancellationToken,
                () => stillControlsSession() && FanStopEntryAdmitted(),
                stillControlsSession, FanStopOwnedContinuationAdmitted).ConfigureAwait(false);

            return result;
        }
        finally
        {
            // Cancellation after possible submission still publishes the fence.
            try { PublishFanStopOutcome(); }
            finally { operationGate.Release(); }
        }
    }

    private ServiceStatusSnapshot ProjectFanStopStatus(ServiceStatusSnapshot current)
    {
        FanStopStatus projected = ProjectAutomaticFanStop(fanStopController.GetStatus(current.Capabilities?.HasFanStopTrialEvidence == true));
        bool writesBlocked = current.WritesBlocked || fanStopController.RequiresRecovery;
        DeviceCapabilities? capabilities = current.Capabilities;
        if (capabilities is not null)
        {
            // Capability is service/backend potential. Current device, session,
            // thermal and ownership admission remains in FanStopStatus.CanStop.
            bool supported = capabilities.HasFanStopTrialEvidence && fanStopController.BackendAvailable;
            if (capabilities.SupportsFanStop != supported)
            { capabilities = capabilities with { SupportsFanStop = supported }; }
        }
        // Unchanged status reads retain the accepted snapshot identity. Creating
        // a record on every read fabricates changes for cancellation/telemetry
        // observers even though no controller transition occurred.
        return current.FanStopStatus == projected && current.WritesBlocked == writesBlocked
            && ReferenceEquals(capabilities, current.Capabilities)
            ? current
            : current with { FanStopStatus = projected, WritesBlocked = writesBlocked, Capabilities = capabilities };
    }

    private async Task MaintainFanStopAsync(CancellationToken token, bool allowAutomaticEntry = true)
    {
        if (!preferences.FanStopSettings.AutomaticEnabled
            && !fanStopController.BlocksOrdinaryThermalWrites
            && (!fanStopController.BackendAvailable
                || GetStatus().Capabilities?.HasFanStopTrialEvidence != true))
        {
            return;
        }

        if (fanStopController.BlocksOrdinaryThermalWrites)
        {
            await operationGate.WaitAsync(token).ConfigureAwait(false);
        }
        else if (!await operationGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            // Idle periodic observation must not queue behind an active transition.
            return;
        }
        try
        {
            ObserveGuardianPowerSource();
            if (fanStopController.Guardian is null && FanStopEntryAdmitted())
            {
                await fanStopController.RefreshAsync(GetStatus().Capabilities?.HasFanStopTrialEvidence == true,
                    () => FanStopCommandAdmitted() && FanStopEntryAdmitted(), token).ConfigureAwait(false);
            }
            await fanStopController.ReconcileAsync(!(fanStopController.Guardian is not null
                    ? FanStopOwnedContinuationAdmitted() : FanStopEntryAdmitted()),
                FanStopCommandAdmitted, token).ConfigureAwait(false);
            ObserveAutomaticFanStopCompletion();
            if (guardianDeferredPowerTransition && !fanStopController.BlocksOrdinaryThermalWrites
                && FanStopOwnedContinuationAdmitted())
            {
                guardianDeferredPowerTransition = false;
                PowerSource source = await ResolveCurrentPowerSourceAsync(token).ConfigureAwait(false);
                if (source != PowerSource.ExternalUnknown)
                {
                    ChangePowerSourceCore(source);
                    await ApplyFullTransitionAsync(source, token).ConfigureAwait(false);
                }
            }
            // Guardian reconciliation can replace its cached status after a
            // completed owner. Read mode-neutral admission exactly once after
            // those updates, so CanStop describes the next possible activation.
            if (fanStopController.Guardian is not null && FanStopEntryAdmitted())
            {
                await fanStopController.RefreshAsync(GetStatus().Capabilities?.HasFanStopTrialEvidence == true,
                    () => FanStopCommandAdmitted() && FanStopEntryAdmitted(), token).ConfigureAwait(false);
            }
            if (allowAutomaticEntry) { await MaintainAutomaticFanStopAsync(token).ConfigureAwait(false); }
        }
        finally
        {
            try { PublishFanStopOutcome(); }
            finally { operationGate.Release(); }
        }
    }

    // Caller owns operationGate. Auto is a generic owned handback, not a Silent
    // transition, and never restores saved manual settings before releasing them.
    private async Task<ControlOperationResult?> ReleaseFanStopCoreAsync(
        CancellationToken token, Func<bool>? stillAuthorized = null)
    {
        PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason.ModeChanged);
        try
        {
            return await fanStopController.ReconcileAsync(true,
                () => stillAuthorized?.Invoke() != false && FanStopCommandAdmitted(), token).ConfigureAwait(false);
        }
        finally { PublishFanStopOutcome(); }
    }

    private bool FanStopCommandAdmitted()
    {
        ServiceStatusSnapshot current = GetStatus();
        return current.DeviceVerified && !current.WritesBlocked
            && !FanControllerAdmissionUnsafe(current)
            && !pendingWriteConfirmations.HasPending
            && !laptopControlExecutor.RequiresReconciliation
            && !hyperBoostJournalSafetyBlock;
    }

    private bool FanStopEntryAdmitted() => ConfigurationIsAdmitted
        && Volatile.Read(ref fanStopShutdownStarted) == 0
        && !lowPowerGate.IsActive && !thermalConvergenceShutdown
        && Volatile.Read(ref pendingPowerAdmissions) == 0
        && Volatile.Read(ref pendingSessionAdmissions) == 0
        && !activeUserActivationIncomplete
        && activeUserStillControlsSession?.Invoke() != false;

    // An entry-only USB-C/profile/power-settling gate must not itself end an
    // owned Stop on a lower-power transition. The owner verifies retained native
    // ownership and independently handles thermal/power uncertainty and increase.
    private bool FanStopOwnedContinuationAdmitted() => ConfigurationIsAdmitted
        && Volatile.Read(ref fanStopShutdownStarted) == 0
        && !lowPowerGate.IsActive && !thermalConvergenceShutdown
        && Volatile.Read(ref pendingSessionAdmissions) == 0
        && !activeUserActivationIncomplete
        && activeUserStillControlsSession?.Invoke() != false;

    private void PublishFanStopOutcome()
    {
        // Confirmation can be followed by caller cancellation or cleanup failure.
        // Retire ordinary ownership before releasing operationGate in every case.
        if (synchronizedFanStopGeneration != fanStopController.ConfirmedStopGeneration)
        {
            synchronizedFanStopGeneration = fanStopController.ConfirmedStopGeneration;
            AdvanceFanAuthorityEpoch();
            if (!fanStopController.OwnsStop)
            {
                recordedFanStopState = FanStopState.Stopped;
                recordedFanStopMessage = "Stop was independently confirmed before subsequent cleanup or cancellation.";
                history.RecordSummary(ControlOperationStatus.Applied,
                    $"Fan Stop/Auto Stopped: {recordedFanStopMessage}");
            }
        }
        RecordFanStopTransition();
        PublishIfChanged(GetStatus());
    }

    private void RecordFanStopTransition()
    {
        FanStopStatus current = fanStopController.GetStatus(true);
        if (current.State is not (FanStopState.Stopped or FanStopState.Automatic or FanStopState.RecoveryRequired
                or FanStopState.Starting or FanStopState.ReturningToAutomatic)
            || current.State == recordedFanStopState && current.Message == recordedFanStopMessage)
        {
            return;
        }
        recordedFanStopState = current.State;
        recordedFanStopMessage = current.Message;
        history.RecordSummary(current.State == FanStopState.RecoveryRequired
                ? ControlOperationStatus.Blocked : ControlOperationStatus.Applied,
            $"Fan Stop/Auto {current.State}: {current.Message}");
    }

    private async Task<ControlOperationResult?> ReleaseFanStopForLifecycleAsync(Func<bool> stillAuthorized)
    {
        PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason.SessionChanged);
        if (!fanStopController.BlocksOrdinaryThermalWrites)
        {
            return null;
        }
        // This phase starts after keyboard Normal release is attempted. It does
        // not inherit an exhausted keyboard/entry budget. Native hangs remain
        // uncovered: cancellation bounds cooperative work, not physical recovery.
        using var cleanup = new CancellationTokenSource(FanStopController.AutomaticCleanupBudget);
        bool acquired = false;
        try
        {
            await operationGate.WaitAsync(cleanup.Token).ConfigureAwait(false);
            acquired = true;
            return await ReleaseFanStopCoreAsync(cleanup.Token, stillAuthorized).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            return new(ControlOperationStatus.Blocked,
                "The independent fan-stop Auto cleanup budget expired; retained ownership or recovery state requires attention.");
        }
        finally
        {
            if (acquired)
            {
                operationGate.Release();
            }
        }
    }

    private async Task<ControlOperationResult> PrepareOwnedFanStopShutdownAsync(
        ControlOperationResult hyperBoostResult, CancellationToken keyboardToken)
    {
        ControlOperationResult? brightness = null;
        ControlOperationResult keyboard = new(ControlOperationStatus.Blocked,
            "Keyboard cleanup did not acquire its shutdown admission.");
        bool acquired = false;
        try
        {
            await operationGate.WaitAsync(keyboardToken).ConfigureAwait(false);
            acquired = true;
            try
            {
                brightness = displayBrightnessReleaseRequired
                    ? await ReleaseDisplayBrightnessUsageCoreAsync(keyboardToken).ConfigureAwait(false) : null;
            }
            catch (OperationCanceledException) when (keyboardToken.IsCancellationRequested)
            {
                brightness = new(ControlOperationStatus.Blocked, "Brightness cleanup exhausted the keyboard shutdown budget.");
            }
            catch (Exception exception)
            {
                brightness = new(ControlOperationStatus.Failed, $"Brightness cleanup failed: {exception.Message}");
            }
            keyboard = await ReleaseDriverModeCoreAsync("shutdown", keyboardToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (keyboardToken.IsCancellationRequested)
        {
            keyboard = new(ControlOperationStatus.Blocked, "Keyboard cleanup exhausted its shutdown budget.");
        }
        catch (Exception exception)
        {
            keyboard = new(ControlOperationStatus.Failed, $"Keyboard cleanup failed: {exception.Message}");
        }
        finally
        {
            if (acquired)
            {
                operationGate.Release();
            }
        }
        ControlOperationResult fan = await ReleaseFanStopForLifecycleAsync(static () => true).ConfigureAwait(false)
            ?? new(ControlOperationStatus.Stored, "No fan-stop ownership requires handback.");
        // The first keyboard attempt is retained. Its specific, command-free
        // delegation refusal is the only reason to retry after confirmed release;
        // uncertain/canceled keyboard operations are never replayed.
        if (keyboard.Message == GuardianHardwareBlockedMessage && !fanStopController.BlocksOrdinaryHardware)
        {
            using var retry = new CancellationTokenSource(FanStopController.AutomaticCleanupBudget);
            await operationGate.WaitAsync(retry.Token).ConfigureAwait(false);
            try
            {
                if (brightness?.Message == GuardianHardwareBlockedMessage)
                { brightness = await ReleaseDisplayBrightnessUsageCoreAsync(retry.Token).ConfigureAwait(false); }
                keyboard = await ReleaseDriverModeCoreAsync("shutdown", retry.Token).ConfigureAwait(false);
            }
            finally { operationGate.Release(); }
        }
        // Owned Auto has its own potentially longer budget. The independent
        // accessory still needs its bounded handback after laptop cleanup;
        // the earlier keyboard token may already have expired.
        ControlOperationResult? pad = await ReleaseCoolingPadForLifecycleAsync(
            allowLowPower: true, CancellationToken.None).ConfigureAwait(false);
        return CombineDisplayBrightnessCleanup(brightness,
            CombineShutdownCleanupResults(hyperBoostResult, pad, null, fan, keyboard));
    }
}
