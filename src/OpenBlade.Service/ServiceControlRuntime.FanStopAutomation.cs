using OpenBlade.Contracts;
using OpenBlade.FanStop;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    private enum AutomaticFanStopEligibilityMode { Disabled, Paused, Armed }
    private sealed record AutomaticFanStopEligibility(long Generation,
        AutomaticFanStopEligibilityMode Mode, FanStopAutomationPauseReason PauseReason);
    private AutomaticFanStopEligibility automaticFanStopEligibility = new(0,
        AutomaticFanStopEligibilityMode.Paused, FanStopAutomationPauseReason.Restarted);
    private long sampledAutomaticFanStopEpoch = -1;
    private long? automaticFanStopCoolSince;
    private long? automaticFanStopLastSample;
    private Guid completedAutomaticFanStopOperation;

    // Ingress can close admission without waiting for a sample or the operation
    // gate. Eligibility and its generation change together: an enable/resume
    // captured before a boundary can never overwrite that boundary's pause.
    // Only the gated monitor owns timestamps.
    private void PauseAutomaticFanStop(FanStopAutomationPauseReason reason)
    {
        AutomaticFanStopEligibility previous;
        do
        {
            previous = Volatile.Read(ref automaticFanStopEligibility);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref automaticFanStopEligibility,
            new(previous.Generation + 1, AutomaticFanStopEligibilityMode.Paused, reason), previous), previous));
    }

    private void PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason reason)
    {
        AutomaticFanStopEligibility previous;
        do
        {
            previous = Volatile.Read(ref automaticFanStopEligibility);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref automaticFanStopEligibility,
            new(previous.Generation + 1,
                previous.Mode == AutomaticFanStopEligibilityMode.Armed ? AutomaticFanStopEligibilityMode.Paused : previous.Mode,
                previous.Mode == AutomaticFanStopEligibilityMode.Armed ? reason : previous.PauseReason), previous), previous));
        // Even an already-paused/disabled boundary invalidates an in-flight arm.
        // Preserve Disabled and its empty reason instead of fabricating a pause.
    }

    private bool ArmAutomaticFanStop(AutomaticFanStopEligibility expected)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref automaticFanStopEligibility,
                new(expected.Generation + 1, AutomaticFanStopEligibilityMode.Armed, FanStopAutomationPauseReason.None), expected), expected))
        { return false; }
        ResetAutomaticFanStopTimer();
        return true;
    }

    private void DisableAutomaticFanStop()
    {
        AutomaticFanStopEligibility previous;
        do
        {
            previous = Volatile.Read(ref automaticFanStopEligibility);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref automaticFanStopEligibility,
            new(previous.Generation + 1, AutomaticFanStopEligibilityMode.Disabled, FanStopAutomationPauseReason.None), previous), previous));
        ResetAutomaticFanStopTimer();
    }

    private void ResetAutomaticFanStopTimer()
    {
        automaticFanStopCoolSince = null;
        automaticFanStopLastSample = null;
        sampledAutomaticFanStopEpoch = Volatile.Read(ref automaticFanStopEligibility).Generation;
    }

    public async Task<ControlOperationResult> SetFanStopSettingsAsync(FanStopSettings settings,
        Func<bool> stillControlsSession, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        if (!FanStopSettings.IsWellFormed(settings))
        { throw new ArgumentException("Fan Stop settings are invalid.", nameof(settings)); }
        EnsureInitialized();
        AutomaticFanStopEligibility expectedEligibility = Volatile.Read(ref automaticFanStopEligibility);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession() || !ConfigurationIsAdmitted
                || userConfigurationRepository is not null && string.IsNullOrWhiteSpace(activeUserSid))
            { return new(ControlOperationStatus.Blocked, "Fan Stop settings require the active Windows session and an available configuration."); }
            if (fanStopController.RequiresRecovery)
            { return new(ControlOperationStatus.Blocked, "Fan Stop recovery must be resolved before its settings can change."); }
            FanStopSettings previous = preferences.FanStopSettings;
            if (previous == settings) { return new(ControlOperationStatus.Stored, "Fan Stop settings are already saved."); }
            bool thermalChanged = previous.EndMode != settings.EndMode || previous.CpuLimitCelsius != settings.CpuLimitCelsius
                || previous.GpuLimitCelsius != settings.GpuLimitCelsius || previous.SecondsAboveLimit != settings.SecondsAboveLimit
                || previous.CpuImmediateCutoffCelsius != settings.CpuImmediateCutoffCelsius
                || previous.GpuImmediateCutoffCelsius != settings.GpuImmediateCutoffCelsius;
            if (thermalChanged && fanStopController.BlocksOrdinaryThermalWrites)
            {
                // The native owner's policy is immutable. Finish its verified
                // typed handback before saving a different thermal policy.
                ControlOperationResult release = await fanStopController.SetModeAsync(FanStopMode.Automatic,
                    GetStatus().Capabilities?.HasFanStopTrialEvidence == true, ResolveCurrentFanIntent(),
                    FanStopCommandAdmitted, cancellationToken, FanStopEntryAdmitted,
                    stillControlsSession, FanStopOwnedContinuationAdmitted).ConfigureAwait(false);
                if (release.Status is ControlOperationStatus.Blocked or ControlOperationStatus.Failed) { return release; }
            }
            if (!stillControlsSession())
            { return new(ControlOperationStatus.Blocked, "The active Windows session changed before Fan Stop settings could be saved."); }
            BatteryAutomationPreferences staged = preferences with { FanStopSettings = settings };
            validator.ValidatePreferences(staged);
            await ConfigurationStore.SaveConfigurationAsync(Automation.Profiles, staged, cancellationToken).ConfigureAwait(false);
            preferences = staged;
            ResetAutomaticFanStopTimer();
            bool stillActive = stillControlsSession();
            if (!settings.AutomaticEnabled) { DisableAutomaticFanStop(); }
            else if (!stillActive) { PauseAutomaticFanStop(FanStopAutomationPauseReason.SessionChanged); }
            else if (!previous.AutomaticEnabled && !ArmAutomaticFanStop(expectedEligibility))
            { return new(ControlOperationStatus.Stored, "Fan Stop settings saved; Automatic Stop remains paused after a concurrent control boundary."); }
            return new(ControlOperationStatus.Stored, "Fan Stop settings saved.");
        }
        catch (ConfigurationQuarantinedException)
        {
            PauseAutomaticFanStop(FanStopAutomationPauseReason.SafetyCondition);
            return ConfigurationQuarantineBlocked();
        }
        finally
        {
            try { PublishFanStopOutcome(); }
            finally { operationGate.Release(); }
        }
    }

    public async Task<ControlOperationResult> ResumeAutomaticFanStopAsync(
        Func<bool> stillControlsSession, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        EnsureInitialized();
        AutomaticFanStopEligibility expectedEligibility = Volatile.Read(ref automaticFanStopEligibility);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession() || !ConfigurationIsAdmitted || !preferences.FanStopSettings.AutomaticEnabled
                || fanStopController.RequiresRecovery || !fanStopController.BackendAvailable)
            { return new(ControlOperationStatus.Blocked, "Automatic Stop cannot resume in the current session or recovery state."); }
            if (!ArmAutomaticFanStop(expectedEligibility))
            { return new(ControlOperationStatus.Blocked, "A control boundary changed while Automatic Stop was resuming; resume again from the active session."); }
            return new(ControlOperationStatus.Stored, "Automatic Stop is waiting for a new cool interval.");
        }
        finally
        {
            try { PublishFanStopOutcome(); }
            finally { operationGate.Release(); }
        }
    }

    private FanStopStatus ProjectAutomaticFanStop(FanStopStatus current)
    {
        FanStopSettings settings = preferences.FanStopSettings;
        AutomaticFanStopEligibility eligibility = Volatile.Read(ref automaticFanStopEligibility);
        FanStopAutomationPauseReason pause = eligibility.PauseReason;
        return current with
        {
            Settings = settings,
            AutomationState = !settings.AutomaticEnabled ? FanStopAutomationState.Disabled
                : current.State is FanStopState.Stopped or FanStopState.Starting ? FanStopAutomationState.Stopped
                : eligibility.Mode == AutomaticFanStopEligibilityMode.Armed ? FanStopAutomationState.Waiting : FanStopAutomationState.Paused,
            AutomationPauseReason = settings.AutomaticEnabled ? pause : FanStopAutomationPauseReason.None,
        };
    }

    private void ObserveAutomaticFanStopCompletion()
    {
        if (fanStopController.Guardian is not { HasOperation: true } guardian) { return; }
        FanStopGuardianSnapshot snapshot = guardian.Snapshot;
        if (snapshot.Unresolved || snapshot.Failure is not null)
        { PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason.SafetyCondition); }
        if (!snapshot.Released || completedAutomaticFanStopOperation == snapshot.OperationId) { return; }
        completedAutomaticFanStopOperation = snapshot.OperationId;
        ResetAutomaticFanStopTimer();
        if (snapshot.Phase != FanStopGuardianPhase.Completed || snapshot.EndReason != FanStopEndReason.ThermalThreshold)
        {
            PauseAutomaticFanStopIfArmed(snapshot.EndReason switch
            {
                FanStopEndReason.PowerChanged => FanStopAutomationPauseReason.PowerChanged,
                FanStopEndReason.SessionChanged => FanStopAutomationPauseReason.SessionChanged,
                FanStopEndReason.CallerRequest => FanStopAutomationPauseReason.ManualAuto,
                _ => FanStopAutomationPauseReason.SafetyCondition,
            });
        }
    }

    // Called only under operationGate, after current ownership is reconciled.
    // Uses the existing one-second fan-maintenance loop, never a second writer.
    private async Task MaintainAutomaticFanStopAsync(CancellationToken token)
    {
        FanStopSettings settings = preferences.FanStopSettings;
        AutomaticFanStopEligibility eligibility = Volatile.Read(ref automaticFanStopEligibility);
        if (fanStopController.RequiresRecovery)
        { PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason.SafetyCondition); ResetAutomaticFanStopTimer(); return; }
        if (!settings.AutomaticEnabled || eligibility.Mode != AutomaticFanStopEligibilityMode.Armed
            || fanStopController.BlocksOrdinaryThermalWrites)
        { ResetAutomaticFanStopTimer(); return; }
        ServiceStatusSnapshot current = GetStatus();
        // Stored intent does not broaden initial entry's existing exact scope.
        if (fanStopController.Guardian is not { } guardian || current.Capabilities?.SupportsFanStop != true
            || current.ActivePowerSource != PowerSource.UsbC || current.ConfirmedState?.Performance != PerformancePreset.Balanced)
        { ResetAutomaticFanStopTimer(); return; }
        // RefreshAsync has just obtained mode-neutral service admission. A
        // failed/denied read is a missing entry observation, not a Stop attempt.
        if (!current.FanStopStatus.CanStop || !FanStopEntryAdmitted() || !FanStopCommandAdmitted())
        { ResetAutomaticFanStopTimer(); return; }
        long epoch = eligibility.Generation;
        if (sampledAutomaticFanStopEpoch != epoch) { ResetAutomaticFanStopTimer(); }
        long started = timeProvider.GetTimestamp();
        FanTemperatureReadings readings;
        using var sampleDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        sampleDeadline.CancelAfter(FanStopThermalPolicy.MaximumSampleAge);
        try { readings = await fanTemperatureSource.ReadForFanStopAsync(sampleDeadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ResetAutomaticFanStopTimer(); throw; }
        catch
        { ResetAutomaticFanStopTimer(); return; }
        long now = timeProvider.GetTimestamp();
        if (!ReferenceEquals(eligibility, Volatile.Read(ref automaticFanStopEligibility))
            || !FanStopEntryAdmitted() || !FanStopCommandAdmitted())
        { ResetAutomaticFanStopTimer(); return; }
        if (!FanStopThermalPolicy.IsValidSample(readings.CpuCelsius, readings.GpuCelsius, readings.GpuPoweredDown,
                timeProvider.GetElapsedTime(started, now), sampleDeadline.IsCancellationRequested))
        { ResetAutomaticFanStopTimer(); return; }
        // Each read has its own bounded age. Count the uncovered interval before
        // it starts, not that interval plus the current valid read's latency.
        // A true missing interval restarts entry; contended/missing telemetry can
        // delay entry indefinitely and never grants credit for unobserved time.
        if (automaticFanStopLastSample is long last && (started < last
            || timeProvider.GetElapsedTime(last, started) > FanStopThermalPolicy.MaximumSampleAge))
        { automaticFanStopCoolSince = null; }
        automaticFanStopLastSample = now;
        bool cool = readings.CpuCelsius < settings.AutomaticCpuBelowCelsius
            && (readings.GpuPoweredDown || readings.GpuCelsius < settings.AutomaticGpuBelowCelsius)
            && (settings.EndMode == FanStopEndMode.Custom || readings.GpuInactive);
        if (!cool) { automaticFanStopCoolSince = null; return; }
        automaticFanStopCoolSince ??= now;
        if (timeProvider.GetElapsedTime(automaticFanStopCoolSince.Value, now) < TimeSpan.FromSeconds(settings.SecondsBelowLimits)) { return; }
        ResetAutomaticFanStopTimer();
        guardian.ConfigureSettings(settings);
        ControlOperationResult result;
        try
        {
            result = await fanStopController.SetModeAsync(FanStopMode.Stop, true, ResolveCurrentFanIntent(),
                FanStopCommandAdmitted, token,
                () => ReferenceEquals(eligibility, Volatile.Read(ref automaticFanStopEligibility)) && FanStopEntryAdmitted(),
                () => ReferenceEquals(eligibility, Volatile.Read(ref automaticFanStopEligibility)) && activeUserStillControlsSession?.Invoke() != false,
                FanStopOwnedContinuationAdmitted).ConfigureAwait(false);
        }
        catch
        { PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason.AttemptFailed); throw; }
        if (result.Status is ControlOperationStatus.Blocked or ControlOperationStatus.Failed)
        { PauseAutomaticFanStopIfArmed(FanStopAutomationPauseReason.AttemptFailed); }
    }
}
