using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal enum CoolingPadLightingBoundaryKind
{
    DisconnectReconnect,
    LowPowerResume,
    ControllerHandoff,
}

internal enum CoolingPadLightingBoundaryStage
{
    InitialBaselinePreset,
    InitialUniformFrame,
    LowPowerBoundaryArmed,
    Disconnected,
    BoundaryCompleted,
    ControllerActive,
    ControllerPresetConfirmed,
    ControllerInactive,
    PostBoundaryBaselinePreset,
    PostBoundaryUniformFrame,
    PostBoundaryBrightnessRestored,
    PostBoundaryPriorPresetResumedWithHandleOpen,
    PostBoundaryPriorPresetResumed,
    PostBoundaryManualPresetRestored,
}

internal enum CoolingPadLightingActiveBoundaryValidationFailure
{
    None,
    Canceled,
    BaselinePresetNotConfirmed,
    OpenFailed,
    BaselineQueryFailed,
    BaselineBrightnessZero,
    FrameSubmissionFailed,
    UniformFrameNotConfirmed,
    PhysicalAbsenceNotConfirmed,
    BoundaryNotConfirmed,
    LowPowerEventNotConfirmed,
    ControllerActivityNotConfirmed,
    ControllerDelegationFailed,
    ControllerPresetNotConfirmed,
    ControllerExitNotConfirmed,
    OwnershipOutcomeNotConfirmed,
    HandleCloseFailed,
    UnexpectedFailure,
}

internal sealed record CoolingPadLightingActiveBoundaryValidationResult(
    CoolingPadLightingBoundaryKind Boundary,
    byte? BaselineBrightnessRaw,
    bool BaselinePresetPhysicallyConfirmed,
    bool FrameSubmissionAttempted,
    bool FrameSubmissionAcknowledged,
    bool UniformFramePhysicallyConfirmed,
    bool HandleOpenAtBoundary,
    bool? PhysicalAbsenceObserved,
    bool LowPowerEventReadbackConfirmed,
    bool? ControllerActiveObserved,
    bool ConcurrentControllerOwnershipAttempted,
    bool? ControllerPresetPhysicallyConfirmed,
    bool? ControllerInactiveObserved,
    CoolingPadLightingSyncState? CoordinatorStateAfterBoundary,
    bool LifecycleCleanupAttempted,
    bool LifecycleCleanupAcknowledged,
    bool BoundaryCompleted,
    bool HandleClosedAfterBoundary,
    bool RetryAttempted,
    bool ManualRecoveryRequired,
    CoolingPadLightingActiveBoundaryValidationFailure Failure)
{
    internal bool Success =>
        Failure == CoolingPadLightingActiveBoundaryValidationFailure.None
        && BaselineBrightnessRaw is > 0
        && BaselinePresetPhysicallyConfirmed
        && FrameSubmissionAttempted
        && FrameSubmissionAcknowledged
        && UniformFramePhysicallyConfirmed
        && HandleOpenAtBoundary
        && BoundaryCompleted
        && HandleClosedAfterBoundary
        && !RetryAttempted
        && Boundary switch
        {
            CoolingPadLightingBoundaryKind.DisconnectReconnect =>
                PhysicalAbsenceObserved == true
                && CoordinatorStateAfterBoundary
                    == CoolingPadLightingSyncState.Faulted
                && !LifecycleCleanupAttempted,
            CoolingPadLightingBoundaryKind.LowPowerResume =>
                LowPowerEventReadbackConfirmed
                && LifecycleCleanupAttempted
                && LifecycleCleanupAcknowledged
                && CoordinatorStateAfterBoundary
                    == CoolingPadLightingSyncState.WaitingForPad,
            CoolingPadLightingBoundaryKind.ControllerHandoff =>
                ControllerActiveObserved == true
                && ConcurrentControllerOwnershipAttempted
                && ControllerPresetPhysicallyConfirmed == true
                && ControllerInactiveObserved == true
                && LifecycleCleanupAttempted
                && LifecycleCleanupAcknowledged
                && CoordinatorStateAfterBoundary
                    == CoolingPadLightingSyncState.WaitingForPad,
            _ => false,
        };
}

internal interface ICoolingPadLightingLowPowerBoundaryObserver
{
    Task<bool> ObserveAsync(
        Func<CancellationToken, Task<bool>> onSuspending,
        CancellationToken cancellationToken);
}

internal sealed class WindowsCoolingPadLightingLowPowerBoundaryObserver(
    Func<IServiceLifecycleEventSource>? createSource = null)
    : ICoolingPadLightingLowPowerBoundaryObserver
{
    private readonly Func<IServiceLifecycleEventSource> createSource =
        createSource ?? (() => new WindowsServiceLifecycleEventSource());

    public async Task<bool> ObserveAsync(
        Func<CancellationToken, Task<bool>> onSuspending,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSuspending);
        IServiceLifecycleEventSource source = createSource();
        try
        {
            bool suspendingObserved = false;
            bool cleanupConfirmed = false;
            await foreach (ServiceLifecycleTrigger trigger in source
                .WatchAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (trigger == ServiceLifecycleTrigger.Suspending
                    && !suspendingObserved)
                {
                    suspendingObserved = true;
                    cleanupConfirmed = await onSuspending(cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (trigger == ServiceLifecycleTrigger.Resumed)
                {
                    return suspendingObserved && cleanupConfirmed;
                }
            }

            return false;
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }
}

internal sealed class CoolingPadLightingActiveBoundaryValidationRunner
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly ICoolingPadPhysicalPresenceSource physicalPresenceSource;
    private readonly Func<SynapseActivityObservation> observeSynapseActivity;
    private readonly ICoolingPadLightingLowPowerBoundaryObserver lowPowerObserver;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadLightingActiveBoundaryValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        ICoolingPadPhysicalPresenceSource physicalPresenceSource,
        Func<SynapseActivityObservation> observeSynapseActivity,
        ICoolingPadLightingLowPowerBoundaryObserver lowPowerObserver,
        TimeProvider? timeProvider = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.physicalPresenceSource = physicalPresenceSource
            ?? throw new ArgumentNullException(nameof(physicalPresenceSource));
        this.observeSynapseActivity = observeSynapseActivity
            ?? throw new ArgumentNullException(nameof(observeSynapseActivity));
        this.lowPowerObserver = lowPowerObserver
            ?? throw new ArgumentNullException(nameof(lowPowerObserver));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadLightingActiveBoundaryValidationRunner CreateWindows() =>
        new(
            static () => new ValidationRunContext(),
            BatteryValidationFactory
                .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory(),
            BatteryValidationFactory.CreateWindowsCoolingPadPhysicalPresenceSource(),
            GpuClockOffsetValidationEnvironment.ObserveSynapseActivity,
            new WindowsCoolingPadLightingLowPowerBoundaryObserver());

    internal async Task<CoolingPadLightingActiveBoundaryValidationResult> RunAsync(
        CoolingPadLightingBoundaryKind boundary,
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(boundary))
        {
            throw new ArgumentOutOfRangeException(nameof(boundary));
        }
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            return Empty(
                boundary,
                CoolingPadLightingActiveBoundaryValidationFailure.UnexpectedFailure);
        }

        var state = new MutableResult(boundary);
        ValidationActor? actor = null;
        CoolingPadLightingLifecycleCoordinator? coordinator = null;
        SemaphoreSlim? operationGate = null;
        bool actorUnavailable = false;
        try
        {
            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadLightingBoundaryStage.InitialBaselinePreset,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                    : CoolingPadLightingActiveBoundaryValidationFailure
                        .BaselinePresetNotConfirmed;
                return state.ToResult();
            }
            state.BaselinePresetPhysicallyConfirmed = true;

            ValidationRunContext context = createContext();
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                    : CoolingPadLightingActiveBoundaryValidationFailure.OpenFailed;
                return state.ToResult();
            }

            bool poisoned = false;
            operationGate = new SemaphoreSlim(1, 1);
            state.OperationGate = operationGate;
            var executor = new CoolingPadLightingExecutor(
                actor.Session,
                context,
                operationGate,
                ExchangeTimeout,
                () => actorUnavailable,
                () => poisoned,
                () => poisoned = true);
            var sink = new ObservedSink(executor);
            coordinator = new CoolingPadLightingLifecycleCoordinator(
                new CoolingPadLightingAdmission(
                    CoolingPadLightingEvidenceGates.UniformForTest(admitted: true),
                    CoolingPadMirrorMapAvailability.Resolved),
                WholeStripCoolingPadKeyboardMirrorMapper.Instance,
                sink,
                minimumFrameInterval: TimeSpan.Zero);

            await coordinator.SetConnectedAsync(true, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await coordinator.SetEnabledAsync(true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                    : CoolingPadLightingActiveBoundaryValidationFailure
                        .BaselineQueryFailed;
                return state.ToResult();
            }
            state.BaselineBrightnessRaw = sink.BaselineBrightnessRaw;
            if (state.BaselineBrightnessRaw is not > 0)
            {
                state.Failure = state.BaselineBrightnessRaw == 0
                    ? CoolingPadLightingActiveBoundaryValidationFailure
                        .BaselineBrightnessZero
                    : CoolingPadLightingActiveBoundaryValidationFailure
                        .BaselineQueryFailed;
                return state.ToResult();
            }

            state.FrameSubmissionAttempted = true;
            coordinator.TryPublish(CreateUniformKeyboardFrame());
            bool submitted;
            try
            {
                submitted = await sink.WaitForFrameAsync(
                        FrameTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                submitted = false;
            }
            state.FrameSubmissionAcknowledged = submitted;
            if (!submitted)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                    : CoolingPadLightingActiveBoundaryValidationFailure
                        .FrameSubmissionFailed;
                return state.ToResult();
            }
            state.ManualRecoveryRequired = true;

            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadLightingBoundaryStage.InitialUniformFrame,
                    cancellationToken).ConfigureAwait(false))
            {
                state.ManualRecoveryRequired = true;
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                    : CoolingPadLightingActiveBoundaryValidationFailure
                        .UniformFrameNotConfirmed;
                return state.ToResult();
            }
            state.UniformFramePhysicallyConfirmed = true;
            state.HandleOpenAtBoundary = actor.Session.Disposition
                != RazerCommandSessionDisposition.FullyDisposed;

            switch (boundary)
            {
                case CoolingPadLightingBoundaryKind.DisconnectReconnect:
                    await RunDisconnectBoundaryAsync(
                            coordinator,
                            actor,
                            confirm,
                            state,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case CoolingPadLightingBoundaryKind.LowPowerResume:
                    await RunLowPowerBoundaryAsync(
                            coordinator,
                            actor,
                            confirm,
                            state,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case CoolingPadLightingBoundaryKind.ControllerHandoff:
                    await RunControllerBoundaryAsync(
                            coordinator,
                            actor,
                            confirm,
                            state,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(boundary));
            }

            if (state.Failure != CoolingPadLightingActiveBoundaryValidationFailure.None)
            {
                return state.ToResult();
            }

            actorUnavailable = true;
            coordinator = null;
            actor = null;
            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.ManualRecoveryRequired |= state.FrameSubmissionAcknowledged;
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure.Canceled;
            return state.ToResult();
        }
        catch
        {
            state.ManualRecoveryRequired |= state.FrameSubmissionAcknowledged;
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure
                .UnexpectedFailure;
            return state.ToResult();
        }
        finally
        {
            if (coordinator is not null && !state.HandleCloseAttempted)
            {
                try
                {
                    await coordinator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    state.ManualRecoveryRequired |= state.FrameSubmissionAcknowledged;
                }
            }
            if (actor is not null && !state.HandleCloseAttempted)
            {
                actorUnavailable = true;
                await DisposeBoundedAsync(actor).ConfigureAwait(false);
            }
            if (!state.BackgroundCleanupPending)
            {
                operationGate?.Dispose();
            }
        }
    }

    private async Task RunDisconnectBoundaryAsync(
        CoolingPadLightingLifecycleCoordinator coordinator,
        ValidationActor actor,
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>> confirm,
        MutableResult state,
        CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync(
                confirm,
                CoolingPadLightingBoundaryStage.Disconnected,
                cancellationToken).ConfigureAwait(false))
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                : CoolingPadLightingActiveBoundaryValidationFailure
                    .PhysicalAbsenceNotConfirmed;
            return;
        }

        state.PhysicalAbsenceObserved = ObservePhysicalAbsence();
        if (state.PhysicalAbsenceObserved != true)
        {
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure
                .PhysicalAbsenceNotConfirmed;
            return;
        }

        await coordinator.SetConnectedAsync(false, cancellationToken)
            .ConfigureAwait(false);
        state.CoordinatorStateAfterBoundary = coordinator.Snapshot.State;
        if (state.CoordinatorStateAfterBoundary != CoolingPadLightingSyncState.Faulted)
        {
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure
                .OwnershipOutcomeNotConfirmed;
            return;
        }
        state.ManualRecoveryRequired = true;

        state.HandleCloseAttempted = true;
        if (!await CloseEpochAsync(coordinator, actor, state).ConfigureAwait(false))
        {
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure
                .HandleCloseFailed;
            return;
        }
        state.HandleClosedAfterBoundary = true;

        state.BoundaryCompleted = await ConfirmAsync(
                confirm,
                CoolingPadLightingBoundaryStage.BoundaryCompleted,
                cancellationToken)
            .ConfigureAwait(false);
        if (!state.BoundaryCompleted)
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                : CoolingPadLightingActiveBoundaryValidationFailure
                    .BoundaryNotConfirmed;
        }
    }

    private async Task RunLowPowerBoundaryAsync(
        CoolingPadLightingLifecycleCoordinator coordinator,
        ValidationActor actor,
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>> confirm,
        MutableResult state,
        CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync(
                confirm,
                CoolingPadLightingBoundaryStage.LowPowerBoundaryArmed,
                cancellationToken).ConfigureAwait(false))
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                : CoolingPadLightingActiveBoundaryValidationFailure
                    .BoundaryNotConfirmed;
            return;
        }

        bool observed = await lowPowerObserver.ObserveAsync(
                async _ =>
                {
                    state.LifecycleCleanupAttempted = true;
                    using var cleanup = new CancellationTokenSource(CleanupTimeout);
                    try
                    {
                        await coordinator.InvalidateAsync(cleanup.Token)
                            .ConfigureAwait(false);
                        state.CoordinatorStateAfterBoundary =
                            coordinator.Snapshot.State;
                        state.LifecycleCleanupAcknowledged =
                            state.CoordinatorStateAfterBoundary
                                == CoolingPadLightingSyncState.WaitingForPad;
                        state.HandleCloseAttempted = true;
                        state.HandleClosedAfterBoundary = await CloseEpochAsync(
                                coordinator,
                                actor,
                                state)
                            .ConfigureAwait(false);
                        return state.LifecycleCleanupAcknowledged
                            && state.HandleClosedAfterBoundary;
                    }
                    catch
                    {
                        state.ManualRecoveryRequired = true;
                        return false;
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        state.LowPowerEventReadbackConfirmed = observed;
        state.BoundaryCompleted = observed;
        if (observed)
        {
            state.ManualRecoveryRequired = false;
        }
        if (!observed)
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                : CoolingPadLightingActiveBoundaryValidationFailure
                    .LowPowerEventNotConfirmed;
        }
    }

    private async Task RunControllerBoundaryAsync(
        CoolingPadLightingLifecycleCoordinator coordinator,
        ValidationActor actor,
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>> confirm,
        MutableResult state,
        CancellationToken cancellationToken)
    {
        bool activePrompt = await ConfirmAsync(
                confirm,
                CoolingPadLightingBoundaryStage.ControllerActive,
                cancellationToken)
            .ConfigureAwait(false);
        state.ControllerActiveObserved = activePrompt
            && ObserveSynapseActivity() == SynapseActivityObservation.Active;
        state.ConcurrentControllerOwnershipAttempted =
            state.ControllerActiveObserved == true
            && actor.Session.Disposition
                != RazerCommandSessionDisposition.FullyDisposed;
        if (state.ControllerActiveObserved != true
            || !state.ConcurrentControllerOwnershipAttempted)
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                : CoolingPadLightingActiveBoundaryValidationFailure
                    .ControllerActivityNotConfirmed;
            return;
        }

        await coordinator.SetDelegatedAsync(true, cancellationToken)
            .ConfigureAwait(false);
        if (coordinator.Snapshot.State != CoolingPadLightingSyncState.Delegated)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure
                .ControllerDelegationFailed;
            return;
        }

        state.ControllerPresetPhysicallyConfirmed = await ConfirmAsync(
                confirm,
                CoolingPadLightingBoundaryStage.ControllerPresetConfirmed,
                cancellationToken)
            .ConfigureAwait(false);
        if (state.ControllerPresetPhysicallyConfirmed != true)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                : CoolingPadLightingActiveBoundaryValidationFailure
                    .ControllerPresetNotConfirmed;
            return;
        }

        bool inactivePrompt = await ConfirmAsync(
                confirm,
                CoolingPadLightingBoundaryStage.ControllerInactive,
                cancellationToken)
            .ConfigureAwait(false);
        state.ControllerInactiveObserved = inactivePrompt
            && ObserveSynapseActivity() == SynapseActivityObservation.Inactive;
        if (state.ControllerInactiveObserved != true)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingActiveBoundaryValidationFailure.Canceled
                : CoolingPadLightingActiveBoundaryValidationFailure
                    .ControllerExitNotConfirmed;
            return;
        }

        state.LifecycleCleanupAttempted = true;
        await coordinator.InvalidateAsync(cancellationToken).ConfigureAwait(false);
        state.CoordinatorStateAfterBoundary = coordinator.Snapshot.State;
        state.LifecycleCleanupAcknowledged =
            state.CoordinatorStateAfterBoundary
                == CoolingPadLightingSyncState.WaitingForPad;
        if (!state.LifecycleCleanupAcknowledged)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure
                .OwnershipOutcomeNotConfirmed;
            return;
        }

        state.HandleCloseAttempted = true;
        state.HandleClosedAfterBoundary = await CloseEpochAsync(
                coordinator,
                actor,
                state)
            .ConfigureAwait(false);
        if (!state.HandleClosedAfterBoundary)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = CoolingPadLightingActiveBoundaryValidationFailure
                .HandleCloseFailed;
            return;
        }
        state.ManualRecoveryRequired = false;
        state.BoundaryCompleted = true;
    }

    private async Task<bool> CloseEpochAsync(
        CoolingPadLightingLifecycleCoordinator coordinator,
        ValidationActor actor,
        MutableResult state)
    {
        Task coordinatorDisposal = coordinator.DisposeAsync().AsTask();
        try
        {
            await coordinatorDisposal.WaitAsync(CleanupTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            state.BackgroundCleanupPending = true;
            pendingDisposals.Retain(
                DisposeActorAfterCoordinatorAsync(
                    coordinatorDisposal,
                    actor,
                    state.OperationGate));
            return false;
        }
        catch
        {
            await DisposeBoundedAsync(actor).ConfigureAwait(false);
            return false;
        }

        Task disposal = actor.DisposeAsync().AsTask();
        try
        {
            await disposal.WaitAsync(CleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            pendingDisposals.Retain(disposal);
            return false;
        }
    }

    private static async Task DisposeActorAfterCoordinatorAsync(
        Task coordinatorDisposal,
        ValidationActor actor,
        SemaphoreSlim? operationGate)
    {
        try
        {
            try
            {
                await coordinatorDisposal.ConfigureAwait(false);
            }
            catch
            {
                // The actor still needs one deterministic close after coordinator
                // cleanup settles, even when that cleanup reported failure.
            }

            await actor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate?.Dispose();
        }
    }

    private bool ObservePhysicalAbsence()
    {
        try
        {
            return physicalPresenceSource.Observe()
                == CoolingPadPhysicalPresenceObservation.Absent;
        }
        catch
        {
            return false;
        }
    }

    private SynapseActivityObservation ObserveSynapseActivity()
    {
        try
        {
            return observeSynapseActivity();
        }
        catch
        {
            return SynapseActivityObservation.ObservationFailed;
        }
    }

    private async Task DisposeBoundedAsync(ValidationActor actor) =>
        await BatteryValidationRun.DisposeBoundedAsync(
                actor,
                timeProvider,
                CleanupTimeout,
                pendingDisposals)
            .ConfigureAwait(false);

    private static AmbientLightingFrame CreateUniformKeyboardFrame()
    {
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (!AmbientLightingFrame.IsHole(slot))
            {
                slots[slot] = CoolingPadLightingLifecycleValidationRunner
                    .ValidationColor;
            }
        }
        return new AmbientLightingFrame(slots);
    }

    private static async Task<bool> ConfirmAsync(
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>> confirm,
        CoolingPadLightingBoundaryStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await confirm(stage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static CoolingPadLightingActiveBoundaryValidationResult Empty(
        CoolingPadLightingBoundaryKind boundary,
        CoolingPadLightingActiveBoundaryValidationFailure failure) =>
        new MutableResult(boundary) { Failure = failure }.ToResult();

    private sealed class ObservedSink(ICoolingPadLightingExecutor executor)
        : ICoolingPadLightingSink
    {
        private readonly TaskCompletionSource<bool> frameSubmitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal byte? BaselineBrightnessRaw { get; private set; }

        public async Task<HidSessionResult<CoolingPadBrightnessReading>>
            QueryBrightnessAsync(CancellationToken cancellationToken)
        {
            HidSessionResult<CoolingPadBrightnessReading> result =
                await executor.QueryBrightnessAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (result.Failure is null)
            {
                BaselineBrightnessRaw = result.Value.Raw;
            }
            return result;
        }

        public async Task<CoolingPadLightingSinkResult> SubmitFrameAsync(
            CoolingPadLightingFrame frame,
            CancellationToken cancellationToken)
        {
            CoolingPadLightingSinkResult result = await executor.SubmitFrameAsync(
                    frame,
                    cancellationToken)
                .ConfigureAwait(false);
            frameSubmitted.TrySetResult(result.Success);
            return result;
        }

        public Task<CoolingPadLightingSinkResult> SetBrightnessAsync(
            byte percent,
            CancellationToken cancellationToken) =>
            executor.SetBrightnessAsync(percent, cancellationToken);

        public Task<CoolingPadLightingSinkResult> SetBrightnessRawAsync(
            byte raw,
            CancellationToken cancellationToken) =>
            executor.SetBrightnessRawAsync(raw, cancellationToken);

        public Task<CoolingPadLightingSinkResult> SetBrightnessRawForLifecycleAsync(
            byte raw,
            CancellationToken cancellationToken) =>
            executor.SetBrightnessRawAsync(raw, cancellationToken);

        internal async Task<bool> WaitForFrameAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            await frameSubmitted.Task.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
    }

    private sealed class MutableResult(CoolingPadLightingBoundaryKind boundary)
    {
        internal byte? BaselineBrightnessRaw { get; set; }
        internal bool BaselinePresetPhysicallyConfirmed { get; set; }
        internal bool FrameSubmissionAttempted { get; set; }
        internal bool FrameSubmissionAcknowledged { get; set; }
        internal bool UniformFramePhysicallyConfirmed { get; set; }
        internal bool HandleOpenAtBoundary { get; set; }
        internal bool? PhysicalAbsenceObserved { get; set; }
        internal bool LowPowerEventReadbackConfirmed { get; set; }
        internal bool? ControllerActiveObserved { get; set; }
        internal bool ConcurrentControllerOwnershipAttempted { get; set; }
        internal bool? ControllerPresetPhysicallyConfirmed { get; set; }
        internal bool? ControllerInactiveObserved { get; set; }
        internal CoolingPadLightingSyncState? CoordinatorStateAfterBoundary
        {
            get;
            set;
        }
        internal bool LifecycleCleanupAttempted { get; set; }
        internal bool LifecycleCleanupAcknowledged { get; set; }
        internal bool BoundaryCompleted { get; set; }
        internal bool HandleCloseAttempted { get; set; }
        internal bool BackgroundCleanupPending { get; set; }
        internal SemaphoreSlim? OperationGate { get; set; }
        internal bool HandleClosedAfterBoundary { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal CoolingPadLightingActiveBoundaryValidationFailure Failure { get; set; }

        internal CoolingPadLightingActiveBoundaryValidationResult ToResult() => new(
            boundary,
            BaselineBrightnessRaw,
            BaselinePresetPhysicallyConfirmed,
            FrameSubmissionAttempted,
            FrameSubmissionAcknowledged,
            UniformFramePhysicallyConfirmed,
            HandleOpenAtBoundary,
            PhysicalAbsenceObserved,
            LowPowerEventReadbackConfirmed,
            ControllerActiveObserved,
            ConcurrentControllerOwnershipAttempted,
            ControllerPresetPhysicallyConfirmed,
            ControllerInactiveObserved,
            CoordinatorStateAfterBoundary,
            LifecycleCleanupAttempted,
            LifecycleCleanupAcknowledged,
            BoundaryCompleted,
            HandleClosedAfterBoundary,
            RetryAttempted: false,
            ManualRecoveryRequired,
            Failure);
    }
}

internal enum CoolingPadLightingBoundaryValidationFailure
{
    None,
    AlreadyRun,
    Canceled,
    ActiveBoundaryValidationFailed,
    PostBoundaryValidationFailed,
    UnexpectedFailure,
}

internal sealed record CoolingPadLightingBoundaryValidationResult(
    CoolingPadLightingBoundaryKind Boundary,
    CoolingPadLightingActiveBoundaryValidationResult ActiveEpochValidation,
    CoolingPadLightingHandbackValidationResult? PostBoundaryValidation,
    bool ReopenedAndQueriedAfterBoundary,
    bool PostBoundaryHandleClosed,
    bool PostBoundaryPriorPresetResumed,
    bool RetryAttempted,
    bool ManualRecoveryRequired,
    bool ProductionWriteAdmitted,
    CoolingPadLightingBoundaryValidationFailure Failure)
{
    internal bool Success => Failure == CoolingPadLightingBoundaryValidationFailure.None
        && ActiveEpochValidation.Success
        && PostBoundaryValidation is { Success: true }
        && ReopenedAndQueriedAfterBoundary
        && PostBoundaryHandleClosed
        && PostBoundaryPriorPresetResumed
        && !RetryAttempted
        && !ManualRecoveryRequired
        && !ProductionWriteAdmitted;
}

internal sealed class CoolingPadLightingBoundaryValidationRunner
{
    private readonly Func<CoolingPadLightingBoundaryKind,
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadLightingActiveBoundaryValidationResult>> runActiveBoundary;
    private readonly Func<
        Func<CoolingPadLightingHandbackValidationStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadLightingHandbackValidationResult>> runHandbackValidation;
    private int used;

    internal CoolingPadLightingBoundaryValidationRunner(
        Func<CoolingPadLightingBoundaryKind,
            Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>>,
            CancellationToken,
            Task<CoolingPadLightingActiveBoundaryValidationResult>> runActiveBoundary,
        Func<
            Func<CoolingPadLightingHandbackValidationStage, CancellationToken, Task<bool>>,
            CancellationToken,
            Task<CoolingPadLightingHandbackValidationResult>> runHandbackValidation)
    {
        this.runActiveBoundary = runActiveBoundary
            ?? throw new ArgumentNullException(nameof(runActiveBoundary));
        this.runHandbackValidation = runHandbackValidation
            ?? throw new ArgumentNullException(nameof(runHandbackValidation));
    }

    internal static CoolingPadLightingBoundaryValidationRunner CreateWindows()
    {
        var active = CoolingPadLightingActiveBoundaryValidationRunner.CreateWindows();
        return new(
            active.RunAsync,
            static (confirm, cancellationToken) =>
                CoolingPadLightingHandbackValidationRunner.CreateWindows().RunAsync(
                    confirm,
                    cancellationToken));
    }

    internal async Task<CoolingPadLightingBoundaryValidationResult> RunAsync(
        CoolingPadLightingBoundaryKind boundary,
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(boundary))
        {
            throw new ArgumentOutOfRangeException(nameof(boundary));
        }
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            return Empty(boundary, CoolingPadLightingBoundaryValidationFailure.AlreadyRun);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Empty(boundary, CoolingPadLightingBoundaryValidationFailure.Canceled);
        }

        CoolingPadLightingActiveBoundaryValidationResult active;
        try
        {
            active = await runActiveBoundary(boundary, confirm, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Empty(boundary, CoolingPadLightingBoundaryValidationFailure.Canceled);
        }
        catch
        {
            return Empty(
                boundary,
                CoolingPadLightingBoundaryValidationFailure.UnexpectedFailure);
        }

        if (!active.Success)
        {
            return CreateResult(
                boundary,
                active,
                postBoundary: null,
                cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingBoundaryValidationFailure.Canceled
                    : CoolingPadLightingBoundaryValidationFailure
                        .ActiveBoundaryValidationFailed);
        }

        CoolingPadLightingHandbackValidationResult postBoundary;
        try
        {
            postBoundary = await runHandbackValidation(
                    (stage, token) => ConfirmAsync(
                        confirm,
                        MapPostBoundaryStage(stage),
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreateResult(
                boundary,
                active,
                postBoundary: null,
                CoolingPadLightingBoundaryValidationFailure.Canceled);
        }
        catch
        {
            return CreateResult(
                boundary,
                active,
                postBoundary: null,
                CoolingPadLightingBoundaryValidationFailure.UnexpectedFailure);
        }

        return CreateResult(
            boundary,
            active,
            postBoundary,
            postBoundary.Success
                ? CoolingPadLightingBoundaryValidationFailure.None
                : cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingBoundaryValidationFailure.Canceled
                    : CoolingPadLightingBoundaryValidationFailure
                        .PostBoundaryValidationFailed);
    }

    private static CoolingPadLightingBoundaryStage MapPostBoundaryStage(
        CoolingPadLightingHandbackValidationStage stage) => stage switch
        {
            CoolingPadLightingHandbackValidationStage.BaselinePreset =>
                CoolingPadLightingBoundaryStage.PostBoundaryBaselinePreset,
            CoolingPadLightingHandbackValidationStage.UniformFrame =>
                CoolingPadLightingBoundaryStage.PostBoundaryUniformFrame,
            CoolingPadLightingHandbackValidationStage.BrightnessRestored =>
                CoolingPadLightingBoundaryStage.PostBoundaryBrightnessRestored,
            CoolingPadLightingHandbackValidationStage
                    .PriorPresetResumedWithHandleOpen =>
                CoolingPadLightingBoundaryStage
                    .PostBoundaryPriorPresetResumedWithHandleOpen,
            CoolingPadLightingHandbackValidationStage.PriorPresetResumed =>
                CoolingPadLightingBoundaryStage.PostBoundaryPriorPresetResumed,
            CoolingPadLightingHandbackValidationStage.ManualPresetRestored =>
                CoolingPadLightingBoundaryStage.PostBoundaryManualPresetRestored,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

    private static async Task<bool> ConfirmAsync(
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>> confirm,
        CoolingPadLightingBoundaryStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await confirm(stage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static CoolingPadLightingBoundaryValidationResult Empty(
        CoolingPadLightingBoundaryKind boundary,
        CoolingPadLightingBoundaryValidationFailure failure) => new(
        boundary,
        new CoolingPadLightingActiveBoundaryValidationResult(
            boundary,
            BaselineBrightnessRaw: null,
            BaselinePresetPhysicallyConfirmed: false,
            FrameSubmissionAttempted: false,
            FrameSubmissionAcknowledged: false,
            UniformFramePhysicallyConfirmed: false,
            HandleOpenAtBoundary: false,
            PhysicalAbsenceObserved: null,
            LowPowerEventReadbackConfirmed: false,
            ControllerActiveObserved: null,
            ConcurrentControllerOwnershipAttempted: false,
            ControllerPresetPhysicallyConfirmed: null,
            ControllerInactiveObserved: null,
            CoordinatorStateAfterBoundary: null,
            LifecycleCleanupAttempted: false,
            LifecycleCleanupAcknowledged: false,
            BoundaryCompleted: false,
            HandleClosedAfterBoundary: false,
            RetryAttempted: false,
            ManualRecoveryRequired: false,
            CoolingPadLightingActiveBoundaryValidationFailure.UnexpectedFailure),
        PostBoundaryValidation: null,
        ReopenedAndQueriedAfterBoundary: false,
        PostBoundaryHandleClosed: false,
        PostBoundaryPriorPresetResumed: false,
        RetryAttempted: false,
        ManualRecoveryRequired: false,
        ProductionWriteAdmitted: false,
        failure);

    private static CoolingPadLightingBoundaryValidationResult CreateResult(
        CoolingPadLightingBoundaryKind boundary,
        CoolingPadLightingActiveBoundaryValidationResult active,
        CoolingPadLightingHandbackValidationResult? postBoundary,
        CoolingPadLightingBoundaryValidationFailure failure) => new(
        boundary,
        active,
        postBoundary,
        ReopenedAndQueriedAfterBoundary:
            postBoundary?.BaselineBrightnessRaw is not null,
        PostBoundaryHandleClosed:
            postBoundary?.HandleDisposalConfirmedBeforeHandbackCheck == true,
        PostBoundaryPriorPresetResumed:
            postBoundary?.PriorPresetResumedAutomatically == true,
        RetryAttempted: active.RetryAttempted,
        ManualRecoveryRequired:
            postBoundary?.ManualRecoveryRequired == true
            || active.ManualRecoveryRequired && postBoundary?.Success != true,
        ProductionWriteAdmitted: false,
        failure);
}

internal sealed class CoolingPadLightingBoundaryValidationCommand(
    Func<CoolingPadLightingBoundaryKind,
        Func<CoolingPadLightingBoundaryStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadLightingBoundaryValidationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string DisconnectReconnectConfirmation =
        "RZ09-0581:4.00:1532:0F43:LIGHTING-123456:ACTIVE-DISCONNECT-RECONNECT:ONCE";
    internal const string LowPowerResumeConfirmation =
        "RZ09-0581:4.00:1532:0F43:LIGHTING-123456:ACTIVE-SLEEP-RESUME:ONCE";
    internal const string ControllerHandoffConfirmation =
        "RZ09-0581:4.00:1532:0F43:LIGHTING-123456:ACTIVE-SYNAPSE-HANDOFF:ONCE";
    internal const string PreflightAction =
        "Action=Confirm a distinctive preset; query brightness; submit one uniform 123456 frame; keep the active ownership handle across the selected boundary; for sleep, clean up and close from the observed suspend callback; for controller handoff, delegate while the same handle is open; then run one post-boundary handback validation";
    internal const string PreflightRecovery =
        "RequiredRecovery=No retry. If the active epoch or post-boundary handback is uncertain, restore the prior preset with the physical lighting button or Synapse";

    internal async Task<int> RunAsync(
        CoolingPadLightingBoundaryKind boundary,
        string[] args,
        CancellationToken cancellationToken)
    {
        string confirmation = boundary switch
        {
            CoolingPadLightingBoundaryKind.DisconnectReconnect =>
                DisconnectReconnectConfirmation,
            CoolingPadLightingBoundaryKind.LowPowerResume => LowPowerResumeConfirmation,
            CoolingPadLightingBoundaryKind.ControllerHandoff =>
                ControllerHandoffConfirmation,
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
        if (args is not ["--confirm-target", var supplied]
            || !supplied.Equals(confirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                $"Usage: OpenBlade.Capture {CommandName(boundary)} "
                + "--confirm-target " + confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadLightingBoundaryValidationResult result = await run(
                boundary,
                (stage, token) => ConfirmAsync(boundary, stage, token),
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                schemaVersion = 3,
                device = new
                {
                    model = TargetBladeDevice.Model,
                    vendorId = "1532",
                    productId = "0F43",
                    revision = "0200",
                    biosVersion = "4.00",
                },
                uniformColor = "123456",
                readOnly = false,
                retryAllowed = false,
                activeOwnershipEpochValidated =
                    result.ActiveEpochValidation.Success,
                handleOpenAtBoundary =
                    result.ActiveEpochValidation.HandleOpenAtBoundary,
                physicalAbsenceReadbackConfirmed =
                    result.ActiveEpochValidation.PhysicalAbsenceObserved == true,
                lowPowerEventReadbackConfirmed =
                    result.ActiveEpochValidation.LowPowerEventReadbackConfirmed,
                controllerProcessHandoffConfirmed =
                    result.ActiveEpochValidation.ControllerActiveObserved == true
                    && result.ActiveEpochValidation
                        .ControllerPresetPhysicallyConfirmed == true
                    && result.ActiveEpochValidation.ControllerInactiveObserved == true,
                concurrentControllerOwnershipAttempted =
                    result.ActiveEpochValidation
                        .ConcurrentControllerOwnershipAttempted,
                priorEffectReadbackAvailable = false,
                postBoundaryHandbackValidated =
                    result.PostBoundaryValidation
                        is { PriorPresetResumedWithHandleOpen: true },
                postBoundaryHandbackRequiresHandleClose =
                    result.PostBoundaryValidation is
                    {
                        PriorPresetResumedWithHandleOpen: false,
                        PriorPresetResumedAutomatically: true,
                    },
                productionAdmitted = false,
                result,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        return result.Success ? 0 : 2;
    }

    private async Task<bool> ConfirmAsync(
        CoolingPadLightingBoundaryKind boundary,
        CoolingPadLightingBoundaryStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadLightingBoundaryStage.InitialBaselinePreset =>
                "INITIAL-BASELINE-PRESET-CONFIRMED",
            CoolingPadLightingBoundaryStage.InitialUniformFrame =>
                "INITIAL-UNIFORM-123456-CONFIRMED",
            CoolingPadLightingBoundaryStage.LowPowerBoundaryArmed =>
                "LOW-POWER-BOUNDARY-ARMED",
            CoolingPadLightingBoundaryStage.Disconnected =>
                "PAD-USB-AND-12V-DISCONNECTED",
            CoolingPadLightingBoundaryStage.BoundaryCompleted =>
                "PAD-DISCONNECT-RECONNECT-COMPLETED",
            CoolingPadLightingBoundaryStage.ControllerActive =>
                "SYNAPSE-ACTIVE-CONFIRMED",
            CoolingPadLightingBoundaryStage.ControllerPresetConfirmed =>
                "SYNAPSE-PRESET-CONFIRMED",
            CoolingPadLightingBoundaryStage.ControllerInactive =>
                "SYNAPSE-FULLY-CLOSED",
            CoolingPadLightingBoundaryStage.PostBoundaryBaselinePreset =>
                "POST-BOUNDARY-BASELINE-PRESET-CONFIRMED",
            CoolingPadLightingBoundaryStage.PostBoundaryUniformFrame =>
                "POST-BOUNDARY-UNIFORM-123456-CONFIRMED",
            CoolingPadLightingBoundaryStage.PostBoundaryBrightnessRestored =>
                "POST-BOUNDARY-BRIGHTNESS-RESTORED-CONFIRMED",
            CoolingPadLightingBoundaryStage
                    .PostBoundaryPriorPresetResumedWithHandleOpen =>
                "POST-BOUNDARY-PRIOR-PRESET-RESUMED-WITH-HANDLE-OPEN",
            CoolingPadLightingBoundaryStage.PostBoundaryPriorPresetResumed =>
                "POST-BOUNDARY-PRIOR-PRESET-RESUMED",
            CoolingPadLightingBoundaryStage.PostBoundaryManualPresetRestored =>
                "POST-BOUNDARY-MANUAL-PRESET-RESTORED",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        string instruction = stage switch
        {
            CoolingPadLightingBoundaryStage.InitialBaselinePreset =>
                "Select a distinctive pad lighting preset with the physical button, then type",
            CoolingPadLightingBoundaryStage.LowPowerBoundaryArmed =>
                "Type this to arm Windows suspend/resume observation, then put Windows to sleep",
            CoolingPadLightingBoundaryStage.Disconnected =>
                "While OpenBlade still owns the active lighting epoch, disconnect both pad USB and 12 V power, wait for removal, then type",
            CoolingPadLightingBoundaryStage.BoundaryCompleted =>
                "Reconnect both pad USB and 12 V power, wait for enumeration, then type",
            CoolingPadLightingBoundaryStage.ControllerActive =>
                "While OpenBlade still holds the active pad handle, launch Synapse, then type",
            CoolingPadLightingBoundaryStage.ControllerPresetConfirmed =>
                "Select and physically confirm a distinctive pad preset in Synapse, then type",
            CoolingPadLightingBoundaryStage.ControllerInactive =>
                "Fully exit Synapse and its controller processes, then type",
            CoolingPadLightingBoundaryStage.PostBoundaryBaselinePreset =>
                "Confirm or manually restore a distinctive pad preset after the active boundary, then type",
            CoolingPadLightingBoundaryStage.PostBoundaryPriorPresetResumed =>
                "After the post-boundary pad handle closes, type this only if the exact prior preset resumed automatically:",
            CoolingPadLightingBoundaryStage
                    .PostBoundaryPriorPresetResumedWithHandleOpen =>
                "While the post-boundary OpenBlade handle remains open, type this only if the exact prior preset resumed automatically:",
            CoolingPadLightingBoundaryStage.PostBoundaryManualPresetRestored =>
                "Restore the prior preset with the physical button or Synapse, then type",
            _ => "Type this after physical confirmation:",
        };
        await output.WriteLineAsync($"{instruction} {expected}").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }

    private static string CommandName(CoolingPadLightingBoundaryKind boundary) =>
        boundary switch
        {
            CoolingPadLightingBoundaryKind.DisconnectReconnect =>
                "validate-cooling-pad-lighting-disconnect-reconnect",
            CoolingPadLightingBoundaryKind.LowPowerResume =>
                "validate-cooling-pad-lighting-low-power-resume",
            CoolingPadLightingBoundaryKind.ControllerHandoff =>
                "validate-cooling-pad-lighting-controller-handoff",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
}
