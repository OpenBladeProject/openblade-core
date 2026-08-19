using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadFanBoundaryKind
{
    DisconnectReconnect,
    LowPowerResume,
}

internal enum CoolingPadFanBoundaryStage
{
    Initial2200,
    Disconnected,
    BoundaryCompleted,
    Retained2200,
    Release,
}

internal enum CoolingPadFanBoundaryValidationFailure
{
    None,
    AlreadyRun,
    Canceled,
    InitialOpenFailed,
    InitialApplyKnownNotApplied,
    InitialApplyIndeterminate,
    InitialPhysicalNotConfirmed,
    InitialHandleCloseFailed,
    PhysicalAbsenceNotConfirmed,
    BoundaryNotConfirmed,
    ReopenFailed,
    RetainedPhysicalNotConfirmed,
    ReleaseKnownNotApplied,
    ReleaseIndeterminate,
    ReleasePhysicalNotConfirmed,
    UnexpectedFailure,
}

internal sealed record CoolingPadFanBoundaryValidationResult(
    CoolingPadFanBoundaryKind Boundary,
    bool InitialApplyAttempted,
    bool InitialApplyAcknowledged,
    bool InitialPhysicalConfirmed,
    bool InitialHandleClosedBeforeBoundary,
    bool? PhysicalAbsenceObserved,
    bool BoundaryCompleted,
    bool ReopenedAfterBoundary,
    bool Retained2200PhysicallyConfirmedBeforeWrite,
    bool ReleaseAttempted,
    bool ReleaseAcknowledged,
    bool ReleasePhysicalConfirmed,
    int WritePlanAttemptCount,
    int AcknowledgedWritePlanCount,
    bool RetryAttempted,
    bool ActiveModeReadbackConfirmed,
    bool PhysicalRpmReadbackConfirmed,
    bool ManualSynapseRecoveryRequired,
    bool ManualPowerCycleMayBeRequired,
    bool ProductionWriteAdmitted,
    CoolingPadFanBoundaryValidationFailure Failure)
{
    internal bool Success => Failure == CoolingPadFanBoundaryValidationFailure.None
        && InitialApplyAttempted
        && InitialApplyAcknowledged
        && InitialPhysicalConfirmed
        && InitialHandleClosedBeforeBoundary
        && (Boundary != CoolingPadFanBoundaryKind.DisconnectReconnect
            || PhysicalAbsenceObserved == true)
        && BoundaryCompleted
        && ReopenedAfterBoundary
        && Retained2200PhysicallyConfirmedBeforeWrite
        && ReleaseAttempted
        && ReleaseAcknowledged
        && ReleasePhysicalConfirmed
        && WritePlanAttemptCount == 2
        && AcknowledgedWritePlanCount == 2
        && !RetryAttempted
        && !ActiveModeReadbackConfirmed
        && !PhysicalRpmReadbackConfirmed
        && !ManualSynapseRecoveryRequired
        && !ManualPowerCycleMayBeRequired
        && !ProductionWriteAdmitted;
}

internal sealed class CoolingPadFanBoundaryValidationRunner
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly ICoolingPadPhysicalPresenceSource physicalPresenceSource;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadFanBoundaryValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        ICoolingPadPhysicalPresenceSource physicalPresenceSource,
        TimeProvider? timeProvider = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.physicalPresenceSource = physicalPresenceSource
            ?? throw new ArgumentNullException(nameof(physicalPresenceSource));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadFanBoundaryValidationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory(),
        BatteryValidationFactory.CreateWindowsCoolingPadPhysicalPresenceSource());

    internal async Task<CoolingPadFanBoundaryValidationResult> RunAsync(
        CoolingPadFanBoundaryKind boundary,
        Func<CoolingPadFanBoundaryStage, CancellationToken, Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(boundary))
        {
            throw new ArgumentOutOfRangeException(nameof(boundary));
        }
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            return Empty(boundary, CoolingPadFanBoundaryValidationFailure.AlreadyRun);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Empty(boundary, CoolingPadFanBoundaryValidationFailure.Canceled);
        }

        var state = new MutableResult(boundary);
        ValidationActor? initialActor = null;
        ValidationActor? recoveryActor = null;
        try
        {
            ValidationRunContext initialContext = createContext();
            HidSessionResult<ValidationActor> initialOpen =
                await actorFactory.OpenAsync(initialContext).ConfigureAwait(false);
            initialActor = initialOpen.Value;
            if (initialOpen.Failure is not null || initialActor is null)
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanBoundaryValidationFailure.Canceled
                    : CoolingPadFanBoundaryValidationFailure.InitialOpenFailed;
                return state.ToResult();
            }

            bool initialPoisoned = false;
            using (var initialGate = new SemaphoreSlim(1, 1))
            {
                var executor = new CoolingPadHostControlExecutor(
                    initialActor.Session,
                    initialContext,
                    initialGate,
                    ExchangeTimeout,
                    () => initialPoisoned = true);
                try
                {
                    state.InitialApplyAttempted = true;
                    state.WritePlanAttemptCount++;
                    await executor.ApplyValidated2200Async(cancellationToken)
                        .ConfigureAwait(false);
                    state.InitialApplyAcknowledged = true;
                    state.AcknowledgedWritePlanCount++;
                }
                catch (BladeWriteException exception)
                {
                    bool knownNotApplied = exception.SubmissionState
                        == WriteSubmissionState.KnownNotApplied;
                    state.Failure = knownNotApplied
                        ? CoolingPadFanBoundaryValidationFailure
                            .InitialApplyKnownNotApplied
                        : CoolingPadFanBoundaryValidationFailure
                            .InitialApplyIndeterminate;
                    state.ManualPowerCycleMayBeRequired =
                        !knownNotApplied || initialPoisoned;
                    state.ManualSynapseRecoveryRequired = !knownNotApplied;
                    return state.ToResult();
                }
            }

            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanBoundaryStage.Initial2200,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanBoundaryValidationFailure.Canceled
                    : CoolingPadFanBoundaryValidationFailure
                        .InitialPhysicalNotConfirmed;
                return state.ToResult();
            }
            state.InitialPhysicalConfirmed = true;

            if (!await CloseExactlyAsync(initialActor).ConfigureAwait(false))
            {
                initialActor = null;
                state.Failure = CoolingPadFanBoundaryValidationFailure
                    .InitialHandleCloseFailed;
                return state.ToResult();
            }
            initialActor = null;
            state.InitialHandleClosedBeforeBoundary = true;

            if (boundary == CoolingPadFanBoundaryKind.DisconnectReconnect)
            {
                if (!await ConfirmAsync(
                        confirm,
                        CoolingPadFanBoundaryStage.Disconnected,
                        cancellationToken).ConfigureAwait(false))
                {
                    state.Failure = cancellationToken.IsCancellationRequested
                        ? CoolingPadFanBoundaryValidationFailure.Canceled
                        : CoolingPadFanBoundaryValidationFailure
                            .PhysicalAbsenceNotConfirmed;
                    return state.ToResult();
                }

                state.PhysicalAbsenceObserved =
                    physicalPresenceSource.Observe()
                    == CoolingPadPhysicalPresenceObservation.Absent;
                if (state.PhysicalAbsenceObserved != true)
                {
                    state.Failure = CoolingPadFanBoundaryValidationFailure
                        .PhysicalAbsenceNotConfirmed;
                    return state.ToResult();
                }
            }

            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanBoundaryStage.BoundaryCompleted,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanBoundaryValidationFailure.Canceled
                    : CoolingPadFanBoundaryValidationFailure.BoundaryNotConfirmed;
                return state.ToResult();
            }
            state.BoundaryCompleted = true;

            ValidationRunContext recoveryContext = createContext();
            HidSessionResult<ValidationActor> recoveryOpen =
                await actorFactory.OpenAsync(recoveryContext).ConfigureAwait(false);
            recoveryActor = recoveryOpen.Value;
            if (recoveryOpen.Failure is not null || recoveryActor is null)
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanBoundaryValidationFailure.Canceled
                    : CoolingPadFanBoundaryValidationFailure.ReopenFailed;
                return state.ToResult();
            }
            state.ReopenedAfterBoundary = true;

            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanBoundaryStage.Retained2200,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanBoundaryValidationFailure.Canceled
                    : CoolingPadFanBoundaryValidationFailure
                        .RetainedPhysicalNotConfirmed;
                return state.ToResult();
            }
            state.Retained2200PhysicallyConfirmedBeforeWrite = true;

            bool recoveryPoisoned = false;
            using (var recoveryGate = new SemaphoreSlim(1, 1))
            {
                var executor = new CoolingPadHostControlExecutor(
                    recoveryActor.Session,
                    recoveryContext,
                    recoveryGate,
                    ExchangeTimeout,
                    () => recoveryPoisoned = true);
                try
                {
                    state.ReleaseAttempted = true;
                    state.WritePlanAttemptCount++;
                    await executor.ReleaseAsync(cancellationToken)
                        .ConfigureAwait(false);
                    state.ReleaseAcknowledged = true;
                    state.AcknowledgedWritePlanCount++;
                }
                catch (BladeWriteException exception)
                {
                    bool knownNotApplied = exception.SubmissionState
                        == WriteSubmissionState.KnownNotApplied;
                    state.Failure = knownNotApplied
                        ? CoolingPadFanBoundaryValidationFailure.ReleaseKnownNotApplied
                        : CoolingPadFanBoundaryValidationFailure.ReleaseIndeterminate;
                    state.ManualPowerCycleMayBeRequired =
                        !knownNotApplied || recoveryPoisoned;
                    state.ManualSynapseRecoveryRequired = true;
                    return state.ToResult();
                }
            }

            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanBoundaryStage.Release,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanBoundaryValidationFailure.Canceled
                    : CoolingPadFanBoundaryValidationFailure
                        .ReleasePhysicalNotConfirmed;
                return state.ToResult();
            }
            state.ReleasePhysicalConfirmed = true;
            state.Failure = CoolingPadFanBoundaryValidationFailure.None;
            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Failure = CoolingPadFanBoundaryValidationFailure.Canceled;
            return state.ToResult();
        }
        catch
        {
            state.Failure = CoolingPadFanBoundaryValidationFailure.UnexpectedFailure;
            return state.ToResult();
        }
        finally
        {
            if (initialActor is not null)
            {
                await DisposeBoundedAsync(initialActor).ConfigureAwait(false);
            }
            if (recoveryActor is not null)
            {
                await DisposeBoundedAsync(recoveryActor).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> CloseExactlyAsync(ValidationActor actor)
    {
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

    private async Task DisposeBoundedAsync(ValidationActor actor) =>
        await BatteryValidationRun.DisposeBoundedAsync(
                actor,
                timeProvider,
                CleanupTimeout,
                pendingDisposals)
            .ConfigureAwait(false);

    private static async Task<bool> ConfirmAsync(
        Func<CoolingPadFanBoundaryStage, CancellationToken, Task<bool>> confirm,
        CoolingPadFanBoundaryStage stage,
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

    private static CoolingPadFanBoundaryValidationResult Empty(
        CoolingPadFanBoundaryKind boundary,
        CoolingPadFanBoundaryValidationFailure failure) =>
        new MutableResult(boundary) { Failure = failure }.ToResult();

    private sealed class MutableResult(CoolingPadFanBoundaryKind boundary)
    {
        internal bool InitialApplyAttempted { get; set; }
        internal bool InitialApplyAcknowledged { get; set; }
        internal bool InitialPhysicalConfirmed { get; set; }
        internal bool InitialHandleClosedBeforeBoundary { get; set; }
        internal bool? PhysicalAbsenceObserved { get; set; }
        internal bool BoundaryCompleted { get; set; }
        internal bool ReopenedAfterBoundary { get; set; }
        internal bool Retained2200PhysicallyConfirmedBeforeWrite { get; set; }
        internal bool ReleaseAttempted { get; set; }
        internal bool ReleaseAcknowledged { get; set; }
        internal bool ReleasePhysicalConfirmed { get; set; }
        internal int WritePlanAttemptCount { get; set; }
        internal int AcknowledgedWritePlanCount { get; set; }
        internal bool ManualSynapseRecoveryRequired { get; set; }
        internal bool ManualPowerCycleMayBeRequired { get; set; }
        internal CoolingPadFanBoundaryValidationFailure Failure { get; set; }

        internal CoolingPadFanBoundaryValidationResult ToResult() => new(
            boundary,
            InitialApplyAttempted,
            InitialApplyAcknowledged,
            InitialPhysicalConfirmed,
            InitialHandleClosedBeforeBoundary,
            PhysicalAbsenceObserved,
            BoundaryCompleted,
            ReopenedAfterBoundary,
            Retained2200PhysicallyConfirmedBeforeWrite,
            ReleaseAttempted,
            ReleaseAcknowledged,
            ReleasePhysicalConfirmed,
            WritePlanAttemptCount,
            AcknowledgedWritePlanCount,
            RetryAttempted: false,
            ActiveModeReadbackConfirmed: false,
            PhysicalRpmReadbackConfirmed: false,
            ManualSynapseRecoveryRequired:
                ManualSynapseRecoveryRequired
                || InitialApplyAcknowledged && !ReleasePhysicalConfirmed,
            ManualPowerCycleMayBeRequired,
            ProductionWriteAdmitted: false,
            Failure);
    }
}

internal sealed class CoolingPadFanBoundaryValidationCommand(
    Func<CoolingPadFanBoundaryKind,
        Func<CoolingPadFanBoundaryStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadFanBoundaryValidationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string DisconnectReconnectConfirmation =
        "RZ09-0581:4.00:1532:0F43:FAN-2200:DISCONNECT-RECONNECT:RELEASE:ONCE";
    internal const string LowPowerResumeConfirmation =
        "RZ09-0581:4.00:1532:0F43:FAN-2200:SLEEP-RESUME:RELEASE:ONCE";

    internal async Task<int> RunAsync(
        CoolingPadFanBoundaryKind boundary,
        string[] args,
        CancellationToken cancellationToken)
    {
        string confirmation = boundary switch
        {
            CoolingPadFanBoundaryKind.DisconnectReconnect =>
                DisconnectReconnectConfirmation,
            CoolingPadFanBoundaryKind.LowPowerResume => LowPowerResumeConfirmation,
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
        if (args is not ["--confirm-target", var supplied]
            || !supplied.Equals(confirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                $"Usage: OpenBlade.Capture {CommandName(boundary)} " +
                "--confirm-target " + confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadFanBoundaryValidationResult result = await run(
                boundary,
                (stage, token) => ConfirmAsync(boundary, stage, token),
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                device = new
                {
                    model = TargetBladeDevice.Model,
                    vendorId = "1532",
                    productId = "0F43",
                    revision = "0200",
                    biosVersion = "4.00",
                },
                productionAdmitted = false,
                retryAllowed = false,
                activeModeReadbackConfirmed = false,
                physicalRpmReadbackConfirmed = false,
                physicalAbsenceReadbackConfirmed =
                    boundary == CoolingPadFanBoundaryKind.DisconnectReconnect
                    && result.PhysicalAbsenceObserved == true,
                lowPowerEventReadbackConfirmed = false,
                result,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (result.ManualSynapseRecoveryRequired)
        {
            await output.WriteLineAsync(
                "Do not retry. Restore the Auto UI state through Synapse; if the pad is flashing red or asleep, disconnect USB and 12 V power before reconnecting.")
                .ConfigureAwait(false);
        }
        return result.Success ? 0 : 2;
    }

    private async Task<bool> ConfirmAsync(
        CoolingPadFanBoundaryKind boundary,
        CoolingPadFanBoundaryStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadFanBoundaryStage.Initial2200 => "FAN-2200-CONFIRMED",
            CoolingPadFanBoundaryStage.Disconnected =>
                "PAD-DISCONNECTED-CONFIRMED",
            CoolingPadFanBoundaryStage.BoundaryCompleted => boundary switch
            {
                CoolingPadFanBoundaryKind.DisconnectReconnect =>
                    "PAD-DISCONNECT-RECONNECT-COMPLETED",
                CoolingPadFanBoundaryKind.LowPowerResume =>
                    "SYSTEM-SLEEP-RESUME-COMPLETED",
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            },
            CoolingPadFanBoundaryStage.Retained2200 =>
                "FAN-2200-STILL-ACTIVE",
            CoolingPadFanBoundaryStage.Release => "FAN-RELEASE-CONFIRMED",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        string instruction = stage switch
        {
            CoolingPadFanBoundaryStage.Initial2200 =>
                "Type this only after physically confirming the initial 2200 RPM response:",
            CoolingPadFanBoundaryStage.Disconnected =>
                "Disconnect both pad USB and 12 V power, wait for Windows device removal, then type:",
            CoolingPadFanBoundaryStage.BoundaryCompleted => boundary switch
            {
                CoolingPadFanBoundaryKind.DisconnectReconnect =>
                    "Reconnect both pad USB and 12 V power, wait for the indicator, then type:",
                CoolingPadFanBoundaryKind.LowPowerResume =>
                    "Put Windows to sleep, resume fully, wait for the pad indicator, then type:",
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            },
            CoolingPadFanBoundaryStage.Retained2200 =>
                "Before OpenBlade sends another command, type this only if the same 2200 RPM response is still physically active:",
            CoolingPadFanBoundaryStage.Release =>
                "Type this only after physically confirming the one-shot release:",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        await output.WriteLineAsync($"{instruction} {expected}").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }

    private static string CommandName(CoolingPadFanBoundaryKind boundary) =>
        boundary switch
        {
            CoolingPadFanBoundaryKind.DisconnectReconnect =>
                "validate-cooling-pad-fan-disconnect-reconnect",
            CoolingPadFanBoundaryKind.LowPowerResume =>
                "validate-cooling-pad-fan-low-power-resume",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
}
