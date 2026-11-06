using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    private readonly FanStopController fanStopController;
    private int fanStopShutdownStarted;
    private long synchronizedFanStopGeneration;
    private FanStopState recordedFanStopState = FanStopState.Inactive;
    private string? recordedFanStopMessage;

    public async Task<ControlOperationResult> SetFanStopModeAsync(
        FanStopMode mode, Func<bool> stillControlsSession, CancellationToken cancellationToken)
    {
        EnsureInitialized();
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
            ControlOperationResult result = await fanStopController.SetModeAsync(
                mode, current.Capabilities?.HasFanStopTrialEvidence == true,
                ResolveCurrentFanIntent(),
                FanStopCommandAdmitted, cancellationToken,
                () => stillControlsSession() && FanStopEntryAdmitted(),
                stillControlsSession).ConfigureAwait(false);

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
        FanStopStatus projected = fanStopController.GetStatus(current.Capabilities?.HasFanStopTrialEvidence == true);
        bool writesBlocked = current.WritesBlocked || fanStopController.RequiresRecovery;
        // Unchanged status reads retain the accepted snapshot identity. Creating
        // a record on every read fabricates changes for cancellation/telemetry
        // observers even though no controller transition occurred.
        return current.FanStopStatus == projected && current.WritesBlocked == writesBlocked
            ? current
            : current with { FanStopStatus = projected, WritesBlocked = writesBlocked };
    }

    private async Task MaintainFanStopAsync(CancellationToken token)
    {
        if (!fanStopController.BlocksOrdinaryThermalWrites
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
            if (FanStopEntryAdmitted())
            {
                await fanStopController.RefreshAsync(GetStatus().Capabilities?.HasFanStopTrialEvidence == true,
                    () => FanStopCommandAdmitted() && FanStopEntryAdmitted(), token).ConfigureAwait(false);
            }
            await fanStopController.ReconcileAsync(!FanStopEntryAdmitted(),
                FanStopCommandAdmitted, token).ConfigureAwait(false);
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
        if (current.State is not (FanStopState.Stopped or FanStopState.Automatic or FanStopState.RecoveryRequired)
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
        return CombineDisplayBrightnessCleanup(brightness,
            CombineShutdownCleanupResults(hyperBoostResult, null, null, fan, keyboard));
    }
}
