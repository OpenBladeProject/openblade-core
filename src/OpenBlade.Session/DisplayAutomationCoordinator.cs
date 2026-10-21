using OpenBlade.Contracts;
using OpenBlade.Display;

namespace OpenBlade.Session;

public enum DisplayAutomationObservation
{
    IgnoredUnconfirmed,
    BaselineEstablished,
    SameSource,
    TransitionScheduled,
}

public sealed record DisplayTransitionResult(
    long Generation,
    PowerSource PowerSource,
    SessionDisplayExecutionResult Result);

public sealed class DisplayAutomationCoordinator : IAsyncDisposable
{
    private const int MaximumTransitionAttempts = 2;
    private static readonly TimeSpan TransitionRetryDelay = TimeSpan.FromSeconds(1);

    private readonly object sync = new();
    private readonly DisplayPowerContextState powerContext;
    private readonly IDisplayProfileApplier applier;
    private readonly Action<PowerSource>? recordReconciledPowerSource;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private readonly Func<DisplayAutomationRecoveryState, bool>? recordRecoveryState;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CancellationTokenSource? transitionCancellation;
    private readonly List<Task> transitionTasks = [];
    private PowerSource? lastConfirmedPowerSource;
    private PowerSource? pendingTransitionPowerSource;
    private DisplayTransitionResult? lastTransitionResult;
    private DisplayAutomationTarget? lastObservedTarget;
    private DisplaySettingsSnapshot? applicationBaseline;
    private DisplayOwnedSettings applicationOwned;
    private DisplayOwnedSettings uncertainFields;
    private bool manualHold;
    private bool automaticWritesPaused;
    private bool automaticWriteInFlight;
    private bool automaticCancellationHold;
    private bool manualOperationInheritedPause;
    private bool manualOperationPending;
    private DisplayOwnedSettings manualOperationInheritedFields;
    private Guid? checkpointApplicationProfileId;
    private long generation;
    private int disposed;

    public DisplayAutomationCoordinator(
        DisplayPowerContextState powerContext,
        IDisplayProfileApplier applier,
        PowerSource? lastReconciledPowerSource = null,
        Action<PowerSource>? recordReconciledPowerSource = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        DisplayAutomationRecoveryState? recoveryState = null,
        Func<DisplayAutomationRecoveryState, bool>? recordRecoveryState = null,
        bool recoveryStateInvalid = false)
    {
        this.powerContext = powerContext ?? throw new ArgumentNullException(nameof(powerContext));
        this.applier = applier ?? throw new ArgumentNullException(nameof(applier));
        if (lastReconciledPowerSource is not null and not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            throw new ArgumentOutOfRangeException(nameof(lastReconciledPowerSource));
        }

        lastConfirmedPowerSource = recoveryState?.LastReconciledPowerSource
            ?? lastReconciledPowerSource;
        this.recordReconciledPowerSource = recordReconciledPowerSource;
        this.retryDelay = retryDelay ?? Task.Delay;
        this.recordRecoveryState = recordRecoveryState;
        manualHold = recoveryState?.ManualHold == true;
        automaticWritesPaused = recoveryStateInvalid
            || recoveryState?.AutomaticWritesPaused == true;
        uncertainFields = recoveryStateInvalid
            ? DisplayOwnedSettings.All
            : new DisplayOwnedSettings(
                recoveryState?.UncertainRefreshRate == true,
                recoveryState?.UncertainHdr == true,
                recoveryState?.UncertainColorProfile == true,
                recoveryState?.UncertainBrightness == true);
        if (recoveryState?.Baseline is { } recoveredBaseline)
        {
            applicationBaseline = new DisplaySettingsSnapshot(
                IsAvailable: true,
                DisplayName: null,
                Width: 0,
                Height: 0,
                CurrentRefreshKey: recoveredBaseline.RefreshRateKey,
                RefreshOptions: [],
                HdrSupported: recoveredBaseline.HdrEnabled.HasValue,
                HdrEnabled: recoveredBaseline.HdrEnabled ?? false,
                ColorProfilesEnabled: recoveredBaseline.ColorProfileKey is not null,
                ActiveColorProfileId: recoveredBaseline.ColorProfileKey,
                ColorProfiles: [],
                Message: null)
            {
                BrightnessPercent = recoveredBaseline.BrightnessPercent,
            };
            applicationOwned = new DisplayOwnedSettings(
                recoveryState.OwnedRefreshRate,
                recoveryState.OwnedHdr,
                recoveryState.OwnedColorProfile,
                recoveryState.OwnedBrightness);
            checkpointApplicationProfileId = recoveryState.ApplicationProfileId;
        }
    }

    public PowerSource? LastConfirmedPowerSource
    {
        get
        {
            lock (sync)
            {
                return lastConfirmedPowerSource;
            }
        }
    }

    public DisplayTransitionResult? LastTransitionResult
    {
        get
        {
            lock (sync)
            {
                return lastTransitionResult;
            }
        }
    }

    public DisplayAutomationObservation Observe(ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Enum.IsDefined(status.ActivePowerSource))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Power source is undefined.");
        }
        ArgumentNullException.ThrowIfNull(status.Profiles);

        lock (sync)
        {
            PowerSource source = status.ActivePowerSource;
            if (source == PowerSource.ExternalUnknown)
            {
                return MarkUnavailableCore();
            }
            if (source is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
            {
                throw new ArgumentOutOfRangeException(nameof(status), "Power source is not confirmed.");
            }

            DisplayAutomationTarget target = ResolveTarget(status, source);
            DisplayAutomationTarget? previousTarget = lastObservedTarget;
            bool hadObservedTarget = previousTarget is not null;
            bool targetChanged = previousTarget != target;
            bool automationTargetChanged = targetChanged
                && (target.ApplicationProfileId.HasValue
                    || previousTarget?.ApplicationProfileId.HasValue == true
                    || applicationBaseline is not null);
            lastObservedTarget = target;

            if (lastConfirmedPowerSource is null)
            {
                lastConfirmedPowerSource = source;
                long initialGeneration = NextGeneration();
                powerContext.Update(source, initialGeneration);
                RecordReconciledPowerSource(source);
                if (!TryRecordRecoveryState(source))
                {
                    LatchDurabilityFailure();
                    return DisplayAutomationObservation.BaselineEstablished;
                }
                if (target.ApplicationProfileId.HasValue && !automaticWritesPaused)
                {
                    ScheduleTransition(target, initialGeneration);
                    return DisplayAutomationObservation.TransitionScheduled;
                }
                return DisplayAutomationObservation.BaselineEstablished;
            }

            if (lastConfirmedPowerSource == source)
            {
                DisplayPowerContext current = powerContext.Capture();
                if (current.PowerSource != source)
                {
                    long recoveredGeneration = NextGeneration();
                    powerContext.Update(source, recoveredGeneration);
                    if (pendingTransitionPowerSource == source
                        && !automaticWritesPaused
                        && !manualHold)
                    {
                        ScheduleTransition(target, recoveredGeneration);
                        return DisplayAutomationObservation.TransitionScheduled;
                    }
                }

                if (automationTargetChanged && automaticWriteInFlight)
                {
                    automaticWritesPaused = true;
                    manualHold = true;
                    automaticCancellationHold = true;
                    long canceledGeneration = NextGeneration();
                    powerContext.Update(source, canceledGeneration);
                    CancelActiveTransition();
                    _ = TryRecordRecoveryState(source);
                    return DisplayAutomationObservation.SameSource;
                }

                if (automationTargetChanged && !manualHold && !automaticWritesPaused
                    && (hadObservedTarget
                        || applicationBaseline is not null
                        || target.ApplicationProfileId.HasValue))
                {
                    long targetGeneration = NextGeneration();
                    powerContext.Update(source, targetGeneration);
                    ScheduleTransition(target, targetGeneration);
                    return DisplayAutomationObservation.TransitionScheduled;
                }

                return DisplayAutomationObservation.SameSource;
            }

            lastConfirmedPowerSource = source;
            long transitionGeneration = NextGeneration();
            powerContext.Update(source, transitionGeneration);
            CancelActiveTransition();
            if (automaticWriteInFlight)
            {
                automaticWritesPaused = true;
                manualHold = true;
                automaticCancellationHold = true;
            }
            applicationBaseline = null;
            applicationOwned = default;
            checkpointApplicationProfileId = null;
            if (automaticWritesPaused)
            {
                _ = TryRecordRecoveryState(source);
                return DisplayAutomationObservation.SameSource;
            }

            manualHold = false;
            if (!TryRecordRecoveryState(source))
            {
                LatchDurabilityFailure();
                return DisplayAutomationObservation.SameSource;
            }
            ScheduleTransition(target, transitionGeneration);
            return DisplayAutomationObservation.TransitionScheduled;
        }
    }

    public bool BeginManualOperation(DisplayProfileSettings affectedSettings)
    {
        ArgumentNullException.ThrowIfNull(affectedSettings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (sync)
        {
            manualOperationInheritedPause = automaticWritesPaused || automaticWriteInFlight;
            manualOperationInheritedFields = uncertainFields;
            manualOperationPending = true;
            manualHold = true;
            automaticWritesPaused = true;
            uncertainFields = uncertainFields.Include(affectedSettings);
            CancelActiveTransition();
            applicationBaseline = null;
            applicationOwned = default;
            checkpointApplicationProfileId = null;
            if (lastConfirmedPowerSource is not { } source
                || !TryRecordRecoveryState(source))
            {
                manualOperationPending = false;
                manualOperationInheritedPause = false;
                uncertainFields = manualOperationInheritedFields;
                manualOperationInheritedFields = default;
                LatchDurabilityFailure();
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Records the certainty of an explicit display mutation. A confirmed
    /// manual mutation clears an older automatic ambiguity while a possibly
    /// submitted mutation keeps all automatic writes paused.
    /// </summary>
    public void CompleteManualOperation(
        SessionDisplayOperationStatus status,
        DisplaySettingsSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (sync)
        {
            manualOperationPending = false;
            DisplayOwnedSettings affectedFields = uncertainFields;
            bool confirmed = status == SessionDisplayOperationStatus.Succeeded
                && HasCompleteReadback(state, uncertainFields);
            if (confirmed)
            {
                automaticWritesPaused = false;
                automaticCancellationHold = false;
                uncertainFields = default;
            }
            else if (status is SessionDisplayOperationStatus.Indeterminate
                or SessionDisplayOperationStatus.Succeeded)
            {
                automaticWritesPaused = true;
            }
            else
            {
                automaticWritesPaused = manualOperationInheritedPause;
                uncertainFields = automaticWritesPaused
                    ? manualOperationInheritedFields
                    : default;
                if (!automaticWritesPaused)
                {
                    automaticCancellationHold = false;
                }
            }
            manualOperationInheritedPause = false;
            manualOperationInheritedFields = default;
            if (lastConfirmedPowerSource is { } source
                && !TryRecordRecoveryState(source))
            {
                uncertainFields = affectedFields;
                LatchDurabilityFailure();
            }
        }
    }

    /// <summary>
    /// Resumes automatic display reconciliation after an explicit query has
    /// successfully established the current display state.
    /// </summary>
    public bool ResumeAfterSuccessfulExplicitRead(DisplaySettingsSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (sync)
        {
            if (!automaticWritesPaused
                || lastConfirmedPowerSource is not { } source
                || !HasCompleteReadback(state, uncertainFields))
            {
                return false;
            }

            DisplayOwnedSettings reconciledFields = uncertainFields;
            automaticWritesPaused = false;
            manualHold = false;
            uncertainFields = default;
            if (!TryRecordRecoveryState(source))
            {
                uncertainFields = reconciledFields;
                LatchDurabilityFailure();
                return false;
            }
            if (lastObservedTarget is not { } target)
            {
                return true;
            }

            long resumedGeneration = NextGeneration();
            powerContext.Update(source, resumedGeneration);
            ScheduleTransition(target, resumedGeneration);
            return true;
        }
    }

    /// <summary>
    /// Suspends writes while the privileged-service subscription is unavailable,
    /// without forgetting the last confirmed source baseline.
    /// </summary>
    public DisplayAutomationObservation MarkUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (sync)
        {
            return MarkUnavailableCore();
        }
    }

    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] observed;
            lock (sync)
            {
                observed = transitionTasks.ToArray();
            }

            await Task.WhenAll(observed).WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (transitionTasks.Count == observed.Length
                    && transitionTasks.All(task => task.IsCompleted))
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        CancellationTokenSource? transition;
        Task[] active;
        lock (sync)
        {
            transition = transitionCancellation;
            transition?.Cancel();
            active = transitionTasks.ToArray();
        }
        try
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        transition?.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task RunTransitionAsync(
        long transitionGeneration,
        DisplayAutomationTarget target,
        CancellationTokenSource ownedCancellation)
    {
        try
        {
            DisplaySettingsSnapshot? baseline;
            DisplayOwnedSettings owned;
            lock (sync)
            {
                baseline = applicationBaseline;
                owned = applicationOwned;
            }
            if (target.ApplicationProfileId.HasValue && baseline is null)
            {
                SessionDisplayExecutionResult query = await applier.QueryAsync(
                    ownedCancellation.Token).ConfigureAwait(false);
                if (!query.Succeeded || !query.State.IsAvailable)
                {
                    lock (sync)
                    {
                        if (generation == transitionGeneration)
                        {
                            pendingTransitionPowerSource = null;
                            lastTransitionResult = new DisplayTransitionResult(
                                transitionGeneration, target.PowerSource, query);
                        }
                    }
                    return;
                }
                baseline = query.State;
                lock (sync)
                {
                    if (generation == transitionGeneration)
                    {
                        applicationBaseline = baseline;
                        checkpointApplicationProfileId = target.ApplicationProfileId;
                    }
                }
            }
            owned = owned.Include(target.ApplicationSettings);
            DisplayProfileSettings settings = MergeDisplayTarget(target, baseline, owned);
            bool admitted;
            lock (sync)
            {
                admitted = generation == transitionGeneration
                    && !ownedCancellation.IsCancellationRequested;
                if (admitted)
                {
                    applicationOwned = owned;
                    checkpointApplicationProfileId = target.ApplicationProfileId;
                    if (settings.IsConfigured)
                    {
                        automaticWriteInFlight = true;
                        uncertainFields = DisplayOwnedSettings.From(settings);
                    }
                    admitted = TryRecordRecoveryState(target.PowerSource);
                    if (!admitted)
                    {
                        automaticWriteInFlight = false;
                        LatchDurabilityFailure();
                        pendingTransitionPowerSource = null;
                        lastTransitionResult = new DisplayTransitionResult(
                            transitionGeneration,
                            target.PowerSource,
                            DurabilityBlocked(baseline));
                    }
                }
            }
            if (!admitted)
            {
                return;
            }

            SessionDisplayExecutionResult? result = null;
            for (int attempt = 1; attempt <= MaximumTransitionAttempts; attempt++)
            {
                result = await applier.ApplyProfileForTransitionAsync(
                        target.PowerSource,
                        transitionGeneration,
                        settings,
                        ownedCancellation.Token)
                    .ConfigureAwait(false);
                if (result.Succeeded
                    || result.Status == SessionDisplayOperationStatus.Indeterminate
                    || attempt == MaximumTransitionAttempts)
                {
                    break;
                }

                await retryDelay(TransitionRetryDelay, ownedCancellation.Token)
                    .ConfigureAwait(false);
            }

            lock (sync)
            {
                automaticWriteInFlight = false;
                pendingTransitionPowerSource = null;
                lastTransitionResult = new DisplayTransitionResult(
                    transitionGeneration,
                    target.PowerSource,
                    result!);
                if (result!.Status == SessionDisplayOperationStatus.Indeterminate)
                {
                    automaticWritesPaused = true;
                    manualHold = true;
                    CancelActiveTransition();
                    _ = TryRecordRecoveryState(lastConfirmedPowerSource ?? target.PowerSource);
                    return;
                }

                if (manualOperationPending)
                {
                    automaticWritesPaused = true;
                    automaticCancellationHold = false;
                    _ = TryRecordRecoveryState(lastConfirmedPowerSource ?? target.PowerSource);
                    return;
                }

                bool isCurrent = generation == transitionGeneration
                    && !ownedCancellation.IsCancellationRequested;
                if (isCurrent && result.Succeeded
                    && !target.ApplicationProfileId.HasValue)
                {
                    applicationBaseline = null;
                    applicationOwned = default;
                    checkpointApplicationProfileId = null;
                }
                DisplayOwnedSettings completedFields = uncertainFields;
                automaticWritesPaused = false;
                uncertainFields = default;
                bool resumeLatestTarget = automaticCancellationHold;
                automaticCancellationHold = false;
                if (resumeLatestTarget)
                {
                    manualHold = false;
                }
                if (!TryRecordRecoveryState(lastConfirmedPowerSource ?? target.PowerSource))
                {
                    uncertainFields = completedFields;
                    LatchDurabilityFailure();
                    lastTransitionResult = new DisplayTransitionResult(
                        transitionGeneration,
                        target.PowerSource,
                        DurabilityBlocked(result.State));
                    CancelActiveTransition();
                    return;
                }

                if (isCurrent)
                {
                    if (result!.Succeeded)
                    {
                        RecordReconciledPowerSource(target.PowerSource);
                    }
                }
                else if (resumeLatestTarget
                    && lastObservedTarget is { } latestTarget
                    && lastConfirmedPowerSource == latestTarget.PowerSource)
                {
                    long resumedGeneration = NextGeneration();
                    powerContext.Update(latestTarget.PowerSource, resumedGeneration);
                    ScheduleTransition(latestTarget, resumedGeneration);
                }
            }
        }
        catch (OperationCanceledException) when (ownedCancellation.IsCancellationRequested)
        {
            lock (sync)
            {
                if (automaticWriteInFlight)
                {
                    automaticWriteInFlight = false;
                    automaticWritesPaused = true;
                    manualHold = true;
                    pendingTransitionPowerSource = null;
                    if (manualOperationPending)
                    {
                        automaticCancellationHold = false;
                    }
                    _ = TryRecordRecoveryState(
                        lastConfirmedPowerSource ?? target.PowerSource);
                }
            }
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                automaticWriteInFlight = false;
                automaticWritesPaused = true;
                manualHold = true;
                pendingTransitionPowerSource = null;
                lastTransitionResult = new DisplayTransitionResult(
                    transitionGeneration,
                    target.PowerSource,
                    IndeterminateFailure(exception.Message));
                _ = TryRecordRecoveryState(
                    lastConfirmedPowerSource ?? target.PowerSource);
            }
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(transitionCancellation, ownedCancellation))
                {
                    transitionCancellation = null;
                }
            }
            ownedCancellation.Dispose();
        }
    }

    private void CancelActiveTransition()
    {
        transitionCancellation?.Cancel();
        transitionCancellation = null;
    }

    private long NextGeneration() => checked(++generation);

    private DisplayAutomationObservation MarkUnavailableCore()
    {
        if (powerContext.Capture().PowerSource != PowerSource.ExternalUnknown)
        {
            powerContext.Update(PowerSource.ExternalUnknown, NextGeneration());
            CancelActiveTransition();
            if (automaticWriteInFlight)
            {
                automaticWritesPaused = true;
                manualHold = true;
                automaticCancellationHold = true;
                if (lastConfirmedPowerSource is { } source)
                {
                    _ = TryRecordRecoveryState(source);
                }
            }
        }
        return DisplayAutomationObservation.IgnoredUnconfirmed;
    }

    private void ScheduleTransition(
        DisplayAutomationTarget target,
        long transitionGeneration)
    {
        CancelActiveTransition();
        pendingTransitionPowerSource = target.PowerSource;
        transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token);
        CancellationTokenSource ownedCancellation = transitionCancellation;
        transitionTasks.RemoveAll(task => task.IsCompleted);
        Task transition = RunTransitionAsync(
            transitionGeneration,
            target,
            ownedCancellation);
        transitionTasks.Add(transition);
    }

    private static PowerProfile SelectProfile(ProfileSet profiles, PowerSource source) =>
        source switch
        {
            PowerSource.PluggedIn => profiles.PluggedIn,
            PowerSource.Battery => profiles.Battery,
            PowerSource.UsbC => profiles.UsbC,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    private static DisplayAutomationTarget ResolveTarget(
        ServiceStatusSnapshot status,
        PowerSource source)
    {
        DisplayProfileSettings baseline = SelectProfile(status.Profiles, source).Display
            ?? DisplayProfileSettings.Unconfigured();
        ApplicationPowerProfileOverride? application = status.ActiveApplicationProfileId is { } id
            ? status.Profiles.ApplicationProfiles.FirstOrDefault(profile =>
                profile.Enabled && profile.Id == id)?.Overrides.ForPowerSource(source)
            : null;
        return new DisplayAutomationTarget(
            source,
            status.ActiveApplicationProfileId,
            baseline,
            application?.Display);
    }

    private static DisplayProfileSettings MergeDisplayTarget(
        DisplayAutomationTarget target,
        DisplaySettingsSnapshot? baseline,
        DisplayOwnedSettings owned)
    {
        DisplayProfileSettings baseSettings = target.BaseSettings;
        DisplayProfileSettings? application = target.ApplicationSettings;
        return new DisplayProfileSettings(
            application?.RefreshRateKey ?? (owned.RefreshRate
                ? baseline?.CurrentRefreshKey
                : baseSettings.RefreshRateKey),
            application?.HdrEnabled ?? (owned.Hdr
                ? baseline?.HdrEnabled
                : baseSettings.HdrEnabled),
            application?.ColorProfileKey ?? (owned.ColorProfile
                ? baseline?.ActiveColorProfileId
                : baseSettings.ColorProfileKey))
        {
            BrightnessPercent = application?.BrightnessPercent ?? (owned.Brightness
                ? baseline?.BrightnessPercent
                : baseSettings.BrightnessPercent),
        };
    }

    private sealed record DisplayAutomationTarget(
        PowerSource PowerSource,
        Guid? ApplicationProfileId,
        DisplayProfileSettings BaseSettings,
        DisplayProfileSettings? ApplicationSettings);

    private readonly record struct DisplayOwnedSettings(
        bool RefreshRate,
        bool Hdr,
        bool ColorProfile,
        bool Brightness)
    {
        internal static DisplayOwnedSettings All { get; } = new(true, true, true, true);

        internal static DisplayOwnedSettings From(DisplayProfileSettings settings) => new(
            settings.RefreshRateKey is not null,
            settings.HdrEnabled.HasValue,
            settings.ColorProfileKey is not null,
            settings.BrightnessPercent.HasValue);

        internal DisplayOwnedSettings Include(DisplayProfileSettings? settings) => new(
            RefreshRate || settings?.RefreshRateKey is not null,
            Hdr || settings?.HdrEnabled.HasValue == true,
            ColorProfile || settings?.ColorProfileKey is not null,
            Brightness || settings?.BrightnessPercent.HasValue == true);
    }

    private void RecordReconciledPowerSource(PowerSource source)
    {
        try
        {
            recordReconciledPowerSource?.Invoke(source);
        }
        catch
        {
            // A best-effort restart checkpoint cannot make display automation fail.
        }
    }

    private bool TryRecordRecoveryState(PowerSource source)
    {
        try
        {
            DisplayProfileSettings? baseline = applicationBaseline is null
                ? null
                : new DisplayProfileSettings(
                    applicationBaseline.CurrentRefreshKey,
                    applicationBaseline.HdrSupported ? applicationBaseline.HdrEnabled : null,
                    applicationBaseline.ActiveColorProfileId)
                {
                    BrightnessPercent = applicationBaseline.BrightnessPercent,
                };
            var state = new DisplayAutomationRecoveryState(
                source,
                checkpointApplicationProfileId,
                baseline,
                applicationOwned.RefreshRate,
                applicationOwned.Hdr,
                applicationOwned.ColorProfile,
                applicationOwned.Brightness,
                manualHold,
                automaticWritesPaused || automaticWriteInFlight,
                uncertainFields.RefreshRate,
                uncertainFields.Hdr,
                uncertainFields.ColorProfile,
                uncertainFields.Brightness);
            // A null recorder is the explicit in-memory compatibility mode used by
            // tests and embedders. The installed Session Agent always supplies one.
            return recordRecoveryState?.Invoke(state) ?? true;
        }
        catch
        {
            return false;
        }
    }

    private void LatchDurabilityFailure()
    {
        automaticWritesPaused = true;
        manualHold = true;
    }

    private static bool HasCompleteReadback(
        DisplaySettingsSnapshot state,
        DisplayOwnedSettings fields) =>
        state.IsAvailable
        && (!fields.RefreshRate || !string.IsNullOrWhiteSpace(state.CurrentRefreshKey))
        && (!fields.Hdr || state.HdrSupported)
        && (!fields.ColorProfile
            || state.HdrEnabled
            || (state.ColorProfilesEnabled
                && !string.IsNullOrWhiteSpace(state.ActiveColorProfileId)))
        && (!fields.Brightness
            || state.BrightnessPercent.HasValue
            && state.SupportedBrightnessLevels.Count > 0);

    private static SessionDisplayExecutionResult DurabilityBlocked(
        DisplaySettingsSnapshot? state) => new(
            SessionDisplayOperationStatus.Blocked,
            "Display automation is paused because its recovery checkpoint could not be saved.",
            state ?? new DisplaySettingsSnapshot(
                IsAvailable: false,
                DisplayName: null,
                Width: 0,
                Height: 0,
                CurrentRefreshKey: null,
                RefreshOptions: [],
                HdrSupported: false,
                HdrEnabled: false,
                ColorProfilesEnabled: false,
                ActiveColorProfileId: null,
                ColorProfiles: [],
                Message: "Display state is unavailable."));

    private static SessionDisplayExecutionResult IndeterminateFailure(string error) => new(
        SessionDisplayOperationStatus.Indeterminate,
        error,
        new DisplaySettingsSnapshot(
            IsAvailable: false,
            DisplayName: null,
            Width: 0,
            Height: 0,
            CurrentRefreshKey: null,
            RefreshOptions: [],
            HdrSupported: false,
            HdrEnabled: false,
            ColorProfilesEnabled: false,
            ActiveColorProfileId: null,
            ColorProfiles: [],
            Message: error));
}
