namespace OpenBlade.Protocol;

internal enum DirectBatteryTransitionValidationStep
{
    BaselineFirstRead,
    BaselineSecondRead,
    Limit70SafetyAdmission,
    Limit70Acknowledged,
    PreOffRead,
    ProtectionOffSafetyAdmission,
    ProtectionOffAcknowledged,
    CandidateRead,
    RestorationSafetyAdmission,
    RestorationAcknowledged,
    RestorationRead,
}

internal enum DirectBatteryTransitionValidationFailure
{
    None,
    OpenFailed,
    BaselineReadFailed,
    BaselinePairMismatch,
    BaselineNotLimit80,
    BaselineTemporaryFullChargeActive,
    SafetyAdmissionLost,
    TransactionBudgetExhausted,
    Limit70WriteIndeterminate,
    PreOffReadFailed,
    PreOffMismatch,
    ProtectionOffWriteIndeterminate,
    CandidateReadFailed,
    CandidateMismatch,
    RestoreWriteIndeterminate,
    RestorationReadFailed,
    RestorationMismatch,
    Canceled,
    UnexpectedFailure,
}

internal sealed record DirectBatteryTransitionState(
    BatteryValidationSetting Protection,
    bool TemporaryFullCharge);

internal sealed record DirectBatteryTransitionValidationResult(
    DirectBatteryTransitionState? Baseline,
    DirectBatteryTransitionState? PreOff,
    DirectBatteryTransitionState? Candidate,
    DirectBatteryTransitionState? Restored,
    IReadOnlyList<DirectBatteryTransitionValidationStep> Steps,
    DirectBatteryTransitionValidationFailure Failure,
    bool ManualRecoveryRequired)
{
    internal bool Success => Failure == DirectBatteryTransitionValidationFailure.None
        && Baseline is not null
        && Restored == Baseline
        && !ManualRecoveryRequired;
}

internal sealed class DirectBatteryTransitionValidationRunner
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly IPerformanceFanValidationSafetyGate safetyGate;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    internal DirectBatteryTransitionValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        IPerformanceFanValidationSafetyGate safetyGate,
        TimeProvider timeProvider,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal async Task<DirectBatteryTransitionValidationResult> RunAsync(
        CancellationToken cancellationToken)
    {
        var state = new RunState();
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            state.Failure = DirectBatteryTransitionValidationFailure.OpenFailed;
            return state.ToResult();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            state.Failure = DirectBatteryTransitionValidationFailure.Canceled;
            return state.ToResult();
        }

        ValidationRunContext context;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
        }
        catch
        {
            state.Failure = DirectBatteryTransitionValidationFailure.OpenFailed;
            return state.ToResult();
        }

        ValidationActor? actor = null;
        try
        {
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                state.Failure = DirectBatteryTransitionValidationFailure.OpenFailed;
                return state.ToResult();
            }

            StateRead firstBaseline = await ReadStateAsync(
                context,
                actor,
                cancellationToken).ConfigureAwait(false);
            if (!firstBaseline.Success)
            {
                state.Failure = MapReadFailure(
                    firstBaseline.Failure,
                    cancellationToken,
                    DirectBatteryTransitionValidationFailure.BaselineReadFailed);
                return state.ToResult();
            }

            DirectBatteryTransitionState baseline = firstBaseline.State!;
            state.Baseline = baseline;
            state.Steps.Add(DirectBatteryTransitionValidationStep.BaselineFirstRead);

            StateRead secondBaseline = await ReadStateAsync(
                context,
                actor,
                cancellationToken).ConfigureAwait(false);
            if (!secondBaseline.Success)
            {
                state.Failure = MapReadFailure(
                    secondBaseline.Failure,
                    cancellationToken,
                    DirectBatteryTransitionValidationFailure.BaselineReadFailed);
                return state.ToResult();
            }

            state.Steps.Add(DirectBatteryTransitionValidationStep.BaselineSecondRead);
            if (secondBaseline.State != baseline)
            {
                state.Failure = DirectBatteryTransitionValidationFailure.BaselinePairMismatch;
                return state.ToResult();
            }

            if (baseline.Protection != BatteryValidationSetting.Limit80)
            {
                state.Failure = DirectBatteryTransitionValidationFailure.BaselineNotLimit80;
                return state.ToResult();
            }

            if (baseline.TemporaryFullCharge)
            {
                state.Failure =
                    DirectBatteryTransitionValidationFailure.BaselineTemporaryFullChargeActive;
                return state.ToResult();
            }

            if (!await AdmitWriteAsync(
                context,
                actor.Session,
                state,
                DirectBatteryTransitionValidationStep.Limit70SafetyAdmission,
                cancellationToken).ConfigureAwait(false))
            {
                return state.ToResult();
            }

            if (!context.TryAllocateTransaction(out byte limit70TransactionId))
            {
                state.Failure = DirectBatteryTransitionValidationFailure.TransactionBudgetExhausted;
                return state.ToResult();
            }

            state.WriteAttempted = true;
            HidSessionResult<HidUnit> limit70Write = await InvokeAsync(() =>
                actor.BatteryCommands.SetAsync(
                    limit70TransactionId,
                    BatteryValidationSetting.Limit70,
                    cancellationToken)).ConfigureAwait(false);
            if (limit70Write.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = MapWriteFailure(
                    limit70Write.Failure,
                    cancellationToken,
                    DirectBatteryTransitionValidationFailure.Limit70WriteIndeterminate);
                return state.ToResult();
            }

            state.Steps.Add(DirectBatteryTransitionValidationStep.Limit70Acknowledged);
            StateRead preOff = await ReadStateAsync(
                context,
                actor,
                cancellationToken).ConfigureAwait(false);
            if (!preOff.Success)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = MapReadFailure(
                    preOff.Failure,
                    cancellationToken,
                    DirectBatteryTransitionValidationFailure.PreOffReadFailed);
                return state.ToResult();
            }

            state.PreOff = preOff.State;
            state.Steps.Add(DirectBatteryTransitionValidationStep.PreOffRead);
            if (state.PreOff != new DirectBatteryTransitionState(
                BatteryValidationSetting.Limit70,
                TemporaryFullCharge: false))
            {
                state.ManualRecoveryRequired = true;
                state.Failure = DirectBatteryTransitionValidationFailure.PreOffMismatch;
                return state.ToResult();
            }

            if (!await AdmitWriteAsync(
                context,
                actor.Session,
                state,
                DirectBatteryTransitionValidationStep.ProtectionOffSafetyAdmission,
                cancellationToken).ConfigureAwait(false))
            {
                state.ManualRecoveryRequired = true;
                return state.ToResult();
            }

            if (!context.TryAllocateTransaction(out byte protectionOffTransactionId))
            {
                state.ManualRecoveryRequired = true;
                state.Failure =
                    DirectBatteryTransitionValidationFailure.TransactionBudgetExhausted;
                return state.ToResult();
            }

            HidSessionResult<HidUnit> protectionOffWrite = await InvokeAsync(() =>
                actor.BatteryCommands.SetAsync(
                    protectionOffTransactionId,
                    BatteryValidationSetting.ProtectionOff,
                    cancellationToken)).ConfigureAwait(false);
            if (protectionOffWrite.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = MapWriteFailure(
                    protectionOffWrite.Failure,
                    cancellationToken,
                    DirectBatteryTransitionValidationFailure.ProtectionOffWriteIndeterminate);
                return state.ToResult();
            }

            state.Steps.Add(DirectBatteryTransitionValidationStep.ProtectionOffAcknowledged);
            StateRead candidate = await ReadStateAsync(
                context,
                actor,
                cancellationToken).ConfigureAwait(false);
            if (!candidate.Success)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = MapReadFailure(
                    candidate.Failure,
                    cancellationToken,
                    DirectBatteryTransitionValidationFailure.CandidateReadFailed);
                return state.ToResult();
            }

            state.Candidate = candidate.State;
            state.Steps.Add(DirectBatteryTransitionValidationStep.CandidateRead);
            if (state.Candidate != new DirectBatteryTransitionState(
                BatteryValidationSetting.ProtectionOff,
                TemporaryFullCharge: false))
            {
                state.ManualRecoveryRequired = true;
                state.Failure = DirectBatteryTransitionValidationFailure.CandidateMismatch;
                return state.ToResult();
            }

            using var cleanup = new CancellationTokenSource(cleanupDrainTimeout);
            if (!await AdmitWriteAsync(
                context,
                actor.Session,
                state,
                DirectBatteryTransitionValidationStep.RestorationSafetyAdmission,
                cleanup.Token).ConfigureAwait(false))
            {
                state.ManualRecoveryRequired = true;
                return state.ToResult();
            }

            if (!context.TryAllocateTransaction(out byte restoreTransactionId))
            {
                state.ManualRecoveryRequired = true;
                state.Failure = DirectBatteryTransitionValidationFailure.TransactionBudgetExhausted;
                return state.ToResult();
            }

            HidSessionResult<HidUnit> restoreWrite = await InvokeAsync(() =>
                actor.BatteryCommands.SetAsync(
                    restoreTransactionId,
                    baseline.Protection,
                    cleanup.Token)).ConfigureAwait(false);
            if (restoreWrite.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = DirectBatteryTransitionValidationFailure.RestoreWriteIndeterminate;
                return state.ToResult();
            }

            state.Steps.Add(DirectBatteryTransitionValidationStep.RestorationAcknowledged);
            StateRead restored = await ReadStateAsync(
                context,
                actor,
                cleanup.Token).ConfigureAwait(false);
            if (!restored.Success)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = DirectBatteryTransitionValidationFailure.RestorationReadFailed;
                return state.ToResult();
            }

            state.Restored = restored.State;
            state.Steps.Add(DirectBatteryTransitionValidationStep.RestorationRead);
            if (state.Restored != baseline)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = DirectBatteryTransitionValidationFailure.RestorationMismatch;
                return state.ToResult();
            }

            state.Failure = DirectBatteryTransitionValidationFailure.None;
            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Failure = DirectBatteryTransitionValidationFailure.Canceled;
            state.ManualRecoveryRequired = state.WriteAttempted && state.Restored != state.Baseline;
            return state.ToResult();
        }
        catch
        {
            state.Failure = DirectBatteryTransitionValidationFailure.UnexpectedFailure;
            state.ManualRecoveryRequired = state.WriteAttempted;
            return state.ToResult();
        }
        finally
        {
            if (actor is not null)
            {
                await BatteryValidationRun.DisposeBoundedAsync(
                    actor,
                    timeProvider,
                    cleanupDrainTimeout,
                    pendingDisposals).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> AdmitWriteAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RunState state,
        DirectBatteryTransitionValidationStep step,
        CancellationToken cancellationToken)
    {
        PerformanceFanValidationSafetyGateResult admission;
        try
        {
            admission = await safetyGate.ValidateAsync(
                context,
                session,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Failure = DirectBatteryTransitionValidationFailure.Canceled;
            return false;
        }
        catch
        {
            state.Failure = DirectBatteryTransitionValidationFailure.SafetyAdmissionLost;
            return false;
        }

        if (!admission.Success)
        {
            state.Failure = admission.Failure == PerformanceFanValidationSafetyFailure.Canceled
                ? DirectBatteryTransitionValidationFailure.Canceled
                : DirectBatteryTransitionValidationFailure.SafetyAdmissionLost;
            return false;
        }

        state.Steps.Add(step);
        return true;
    }

    private static async Task<StateRead> ReadStateAsync(
        ValidationRunContext context,
        ValidationActor actor,
        CancellationToken cancellationToken)
    {
        if (!context.TryAllocateTransaction(out byte protectionTransactionId))
        {
            return StateRead.Failed(HidSessionFailureKind.Exhausted);
        }

        HidSessionResult<BatteryValidationSetting> protection = await InvokeAsync(() =>
            actor.BatteryCommands.QueryAsync(
                protectionTransactionId,
                cancellationToken)).ConfigureAwait(false);
        if (protection.Failure is not null || !Enum.IsDefined(protection.Value))
        {
            return StateRead.Failed(protection.Failure ?? HidSessionFailureKind.ProtocolFailure);
        }

        if (!context.TryAllocateTransaction(out byte temporaryTransactionId))
        {
            return StateRead.Failed(HidSessionFailureKind.Exhausted);
        }

        HidSessionResult<bool> temporary = await InvokeAsync(() =>
            actor.TemporaryFullChargeCommands.QueryAsync(
                temporaryTransactionId,
                cancellationToken)).ConfigureAwait(false);
        return temporary.Failure is null
            ? StateRead.Succeeded(new DirectBatteryTransitionState(
                protection.Value,
                temporary.Value))
            : StateRead.Failed(temporary.Failure.Value);
    }

    private static async Task<HidSessionResult<T>> InvokeAsync<T>(
        Func<Task<HidSessionResult<T>>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(default, HidSessionFailureKind.Canceled);
        }
        catch
        {
            return new(default, HidSessionFailureKind.NativeIoFailure);
        }
    }

    private static DirectBatteryTransitionValidationFailure MapReadFailure(
        HidSessionFailureKind? failure,
        CancellationToken cancellationToken,
        DirectBatteryTransitionValidationFailure fallback) =>
        cancellationToken.IsCancellationRequested || failure == HidSessionFailureKind.Canceled
            ? DirectBatteryTransitionValidationFailure.Canceled
            : fallback;

    private static DirectBatteryTransitionValidationFailure MapWriteFailure(
        HidSessionFailureKind? failure,
        CancellationToken cancellationToken,
        DirectBatteryTransitionValidationFailure fallback) =>
        cancellationToken.IsCancellationRequested || failure == HidSessionFailureKind.Canceled
            ? DirectBatteryTransitionValidationFailure.Canceled
            : fallback;

    private readonly record struct StateRead(
        DirectBatteryTransitionState? State,
        HidSessionFailureKind? Failure)
    {
        internal bool Success => State is not null && Failure is null;

        internal static StateRead Succeeded(DirectBatteryTransitionState state) =>
            new(state, null);

        internal static StateRead Failed(HidSessionFailureKind failure) =>
            new(null, failure);
    }

    private sealed class RunState
    {
        internal DirectBatteryTransitionState? Baseline { get; set; }
        internal DirectBatteryTransitionState? PreOff { get; set; }
        internal DirectBatteryTransitionState? Candidate { get; set; }
        internal DirectBatteryTransitionState? Restored { get; set; }
        internal List<DirectBatteryTransitionValidationStep> Steps { get; } = [];
        internal DirectBatteryTransitionValidationFailure Failure { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal bool WriteAttempted { get; set; }

        internal DirectBatteryTransitionValidationResult ToResult() => new(
            Baseline,
            PreOff,
            Candidate,
            Restored,
            Steps.ToArray(),
            Failure,
            ManualRecoveryRequired);
    }
}
