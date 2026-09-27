namespace OpenBlade.Protocol;

public enum PerformanceFanValidationTarget
{
    Balanced,
    Silent,
    Performance,
    Fixed5400,
    BatterySaver,
    BatteryBalanced,
    HyperBoost,
    HyperBoostFromSilent,
    HyperBoostFromPerformance,
}

public enum PerformanceFanValidationFailure
{
    None,
    WrongTarget,
    Conflict,
    ConflictDetectionFailed,
    OpenFailed,
    BaselineReadFailed,
    BaselinePairMismatch,
    BaselineFixedSpeedCannotBeRestored,
    TargetAlreadyActive,
    ApplyWriteIndeterminate,
    CandidateReadFailed,
    CandidatePairMismatch,
    CandidateTargetMismatch,
    RestoreWriteIndeterminate,
    RestorationReadFailed,
    RestorationPairMismatch,
    RestorationMismatch,
    FixedFanRpmReadFailed,
    FixedFanRpmOutOfRange,
    Canceled,
    BaselineNotBalanced,
    BaselineNotRequiredBaseMode = 21,
    SustainedValidationFailed = 22,
}

public sealed record PerformanceFanThermalPair(
    string Thermal1Hex,
    string Thermal2Hex);

public sealed record PerformanceFanSetterValidationResult(
    PerformanceFanValidationTarget Target,
    PerformanceFanThermalPair? Baseline,
    PerformanceFanThermalPair? Candidate,
    FanRpmReadings? CandidateFanRpm,
    IReadOnlyList<FanRpmReadings> CandidateFanRpmSamples,
    PerformanceFanThermalPair? Restoration,
    bool ApplyAttempted,
    bool ApplyAcknowledged,
    bool CandidateConfirmed,
    bool RestoreAttempted,
    bool RestoreAcknowledged,
    bool Restored,
    bool IndeterminatePartialWrite,
    bool ManualRecoveryRequired,
    PerformanceFanValidationFailure Failure)
{
    internal PerformanceFanThermalPair? ObserverFailureReconciliation { get; init; }
    internal bool FullPowerAdapterConfirmed { get; init; }

    internal OpenBlade.Contracts.AdapterPowerState InitialAdapterPowerState
    { get; init; }

    internal bool CoolingPadAbsentConfirmed { get; init; }

    internal bool CoolingPadPresentConfirmed { get; init; }

    internal bool CoolingPadOpaqueZeroConfirmed { get; init; }

    internal bool PostRestorationFullPowerAdapterConfirmed { get; init; }

    internal bool PostRestorationCoolingPadAbsentConfirmed { get; init; }

    internal PerformanceFanValidationSafetyFailure PostRestorationSafetyFailure
    { get; init; }

    internal PerformanceFanValidationSafetyFailure SafetyFailure { get; init; }

    internal bool BaselineBalancedConfirmed { get; init; }

    internal bool BaselineRequiredBaseModeConfirmed { get; init; }

    internal bool BalancedIntermediaryObserved { get; init; }

    internal HyperBoostSustainedValidationResult? SustainedValidation { get; init; }

    internal HyperBoostAdapterTransitionValidationResult? AdapterTransitionValidation
    { get; init; }

    internal HyperBoostPowerSourceBoundaryValidationResult? PowerSourceBoundaryValidation
    { get; init; }

    public bool Success => Failure == PerformanceFanValidationFailure.None
        && ApplyAcknowledged
        && CandidateConfirmed
        && Restored;
}

internal enum PerformanceFanValidationSafetyFailure
{
    None,
    AdapterPowerReadFailed,
    FullPowerAdapterRequired,
    CoolingPadPresent,
    CoolingPadAbsenceObservationFailed,
    CoolingPadAbsent,
    CoolingPadAmbiguous,
    CoolingPadObservationFailed,
    CoolingPadQueryFailed,
    CoolingPadOpaqueZeroRequired,
    Canceled,
}

internal readonly record struct PerformanceFanValidationSafetyGateResult(
    bool FullPowerAdapterConfirmed,
    bool CoolingPadAbsentConfirmed,
    bool CoolingPadPresentConfirmed,
    bool CoolingPadOpaqueZeroConfirmed,
    PerformanceFanValidationSafetyFailure Failure)
{
    internal OpenBlade.Contracts.AdapterPowerState AdapterPowerState { get; init; }

    internal PerformanceFanValidationSafetyGateResult(
        bool FullPowerAdapterConfirmed,
        bool CoolingPadAbsentConfirmed,
        PerformanceFanValidationSafetyFailure Failure)
        : this(
            FullPowerAdapterConfirmed,
            CoolingPadAbsentConfirmed,
            CoolingPadPresentConfirmed: false,
            CoolingPadOpaqueZeroConfirmed: false,
            Failure)
    {
    }

    internal bool Success => Failure == PerformanceFanValidationSafetyFailure.None
        && FullPowerAdapterConfirmed
        && (CoolingPadAbsentConfirmed
            || CoolingPadPresentConfirmed && CoolingPadOpaqueZeroConfirmed);
}

internal interface IPerformanceFanValidationSafetyGate
{
    Task<PerformanceFanValidationSafetyGateResult> ValidateAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken);
}

internal sealed class PadAbsentHyperBoostSafetyGate(
    ICoolingPadPhysicalPresenceSource coolingPadPresence,
    TimeSpan exchangeTimeout) : IPerformanceFanValidationSafetyGate
{
    private readonly ICoolingPadPhysicalPresenceSource coolingPadPresence =
        coolingPadPresence ?? throw new ArgumentNullException(nameof(coolingPadPresence));
    private readonly TimeSpan exchangeTimeout = ValidateTimeout(exchangeTimeout);

    public async Task<PerformanceFanValidationSafetyGateResult> ValidateAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);

        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            return Failure(PerformanceFanValidationSafetyFailure.AdapterPowerReadFailed);
        }

        var adapter = new AdapterPowerCommandClient(session, exchangeTimeout);
        HidSessionResult<OpenBlade.Contracts.AdapterPowerState> power =
            await adapter.QueryAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (power.Failure is not null)
        {
            return Failure(cancellationToken.IsCancellationRequested
                ? PerformanceFanValidationSafetyFailure.Canceled
                : PerformanceFanValidationSafetyFailure.AdapterPowerReadFailed);
        }

        if (!HyperBoostValidationPower.IsAdmittedFullPower(power.Value))
        {
            return Failure(PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired);
        }

        return coolingPadPresence.Observe() switch
        {
            CoolingPadPhysicalPresenceObservation.Absent => new(
                FullPowerAdapterConfirmed: true,
                CoolingPadAbsentConfirmed: true,
                CoolingPadPresentConfirmed: false,
                CoolingPadOpaqueZeroConfirmed: false,
                PerformanceFanValidationSafetyFailure.None)
            { AdapterPowerState = power.Value },
            CoolingPadPhysicalPresenceObservation.Present => new(
                FullPowerAdapterConfirmed: true,
                CoolingPadAbsentConfirmed: false,
                CoolingPadPresentConfirmed: false,
                CoolingPadOpaqueZeroConfirmed: false,
                PerformanceFanValidationSafetyFailure.CoolingPadPresent)
            { AdapterPowerState = power.Value },
            _ => new(
                FullPowerAdapterConfirmed: true,
                CoolingPadAbsentConfirmed: false,
                CoolingPadPresentConfirmed: false,
                CoolingPadOpaqueZeroConfirmed: false,
                PerformanceFanValidationSafetyFailure.CoolingPadAbsenceObservationFailed)
            { AdapterPowerState = power.Value },
        };
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        RazerCommandSessionTimeout.Validate(timeout);
        return timeout;
    }

    private static PerformanceFanValidationSafetyGateResult Failure(
        PerformanceFanValidationSafetyFailure failure) => new(
        FullPowerAdapterConfirmed: false,
        CoolingPadAbsentConfirmed: false,
        CoolingPadPresentConfirmed: false,
        CoolingPadOpaqueZeroConfirmed: false,
        failure);
}


public interface IPerformanceFanSetterValidationRunner
{
    Task<PerformanceFanSetterValidationResult> RunAsync(
        PerformanceFanValidationTarget target,
        CancellationToken cancellationToken);
}

internal interface IHyperBoostAdapterTransitionValidationRunner
{
    Task<PerformanceFanSetterValidationResult> RunAsync(
        Func<CancellationToken, Task<HyperBoostAdapterTransitionAuthorization>>
            authorizeTransition,
        CancellationToken cancellationToken);
}

internal enum ExactHyperBoostValidationStage { Baseline, Candidate, Restored }

internal delegate Task<bool> ExactHyperBoostValidationObserver(
    ExactHyperBoostValidationStage stage, Func<CancellationToken, Task<bool>> guard,
    CancellationToken cancellationToken);

internal sealed class PerformanceFanSetterValidationRunner
    : IPerformanceFanSetterValidationRunner,
      IHyperBoostAdapterTransitionValidationRunner,
      IHyperBoostPowerSourceBoundaryValidationRunner
{
    private const byte CommandClass = 0x0D;
    private const byte QueryCommandId = 0x82;
    private const byte SetStateCommandId = 0x02;
    private const byte SetSpeedCommandId = 0x01;
    private const int FixedFanMinimumRpm = 5000;
    private const int FixedFanMaximumRpm = 6000;
    private const int FixedFanRequiredConsecutiveSamples = 3;
    private const int DefaultFixedFanMaximumSamples = 120;
    private const int DefaultFixedFanStateCheckEverySamples = 5;
    private static readonly TimeSpan DefaultFixedFanSampleInterval =
        TimeSpan.FromSeconds(1);
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly TimeSpan fixedFanSampleInterval;
    private readonly int fixedFanMaximumSamples;
    private readonly int fixedFanStateCheckEverySamples;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly IPerformanceFanValidationSafetyGate? safetyGate;
    private readonly ReviewedHyperBoostThermalPlan? sustainedPlan;
    private readonly IHyperBoostThermalTelemetrySource? thermalTelemetrySource;
    private readonly IHyperBoostExternalWorkloadSource? externalWorkloadSource;
    private readonly ReviewedHyperBoostAdapterTransitionPlan? adapterTransitionPlan;
    private readonly ReviewedHyperBoostPowerSourceBoundaryPlan? powerSourceBoundaryPlan;
    private readonly ExactHyperBoostValidationObserver? exactObserver;
    private readonly Rz090528PadlessNoLoadValidation? padlessNoLoad;
    private int started;

    internal PerformanceFanSetterValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry? pendingDisposals = null,
        TimeSpan? fixedFanSampleInterval = null,
        int fixedFanMaximumSamples = DefaultFixedFanMaximumSamples,
        int fixedFanStateCheckEverySamples = DefaultFixedFanStateCheckEverySamples,
        IPerformanceFanValidationSafetyGate? safetyGate = null,
        ReviewedHyperBoostThermalPlan? sustainedPlan = null,
        IHyperBoostThermalTelemetrySource? thermalTelemetrySource = null,
        IHyperBoostExternalWorkloadSource? externalWorkloadSource = null,
        ReviewedHyperBoostAdapterTransitionPlan? adapterTransitionPlan = null,
        ReviewedHyperBoostPowerSourceBoundaryPlan? powerSourceBoundaryPlan = null,
        ExactHyperBoostValidationObserver? exactObserver = null,
        Rz090528PadlessNoLoadValidation? padlessNoLoad = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.fixedFanSampleInterval = fixedFanSampleInterval
            ?? DefaultFixedFanSampleInterval;
        if (this.fixedFanSampleInterval < TimeSpan.Zero
            || this.fixedFanSampleInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(fixedFanSampleInterval));
        }

        if (fixedFanMaximumSamples < FixedFanRequiredConsecutiveSamples
            || fixedFanMaximumSamples > DefaultFixedFanMaximumSamples)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedFanMaximumSamples));
        }

        if (fixedFanStateCheckEverySamples <= 0
            || fixedFanStateCheckEverySamples > fixedFanMaximumSamples)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedFanStateCheckEverySamples));
        }

        this.fixedFanMaximumSamples = fixedFanMaximumSamples;
        this.fixedFanStateCheckEverySamples = fixedFanStateCheckEverySamples;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
        this.safetyGate = safetyGate;
        if ((sustainedPlan is null) != (thermalTelemetrySource is null)
            || sustainedPlan?.WorkloadKind ==
                    HyperBoostValidationWorkloadKind.ExternalReviewedLoad
                && (externalWorkloadSource is null
                    || !sustainedPlan.HasBoundRuntimeExactExecutable)
            || sustainedPlan?.WorkloadKind ==
                    HyperBoostValidationWorkloadKind.SustainedNoLoadDwell
                && externalWorkloadSource is not null
            || sustainedPlan is null && externalWorkloadSource is not null
            || adapterTransitionPlan is not null
                && (sustainedPlan is not null
                    || thermalTelemetrySource is not null
                    || externalWorkloadSource is not null
                    || powerSourceBoundaryPlan is not null)
            || powerSourceBoundaryPlan is not null
                && (sustainedPlan is not null
                    || thermalTelemetrySource is not null
                    || externalWorkloadSource is not null
                    || adapterTransitionPlan is not null))
        {
            throw new ArgumentException(
                "Sustained validation requires a matching thermal plan, telemetry source, " +
                "and external-workload observer only for an external-load plan; adapter " +
                "transition and power-source boundary validation must each use their own " +
                "reviewed plan.");
        }

        if (sustainedPlan is not null && safetyGate is null)
        {
            throw new ArgumentException(
                "Sustained pad-absent HyperBoost validation requires the safety gate.");
        }

        this.sustainedPlan = sustainedPlan;
        this.thermalTelemetrySource = thermalTelemetrySource;
        this.externalWorkloadSource = externalWorkloadSource;
        this.adapterTransitionPlan = adapterTransitionPlan;
        this.powerSourceBoundaryPlan = powerSourceBoundaryPlan;
        this.exactObserver = exactObserver;
        if (padlessNoLoad is not null && (exactObserver is not null || safetyGate is not null || sustainedPlan is not null))
        {
            throw new ArgumentException("The exact padless no-load experiment requires its own composition.");
        }

        this.padlessNoLoad = padlessNoLoad;
    }

    public Task<PerformanceFanSetterValidationResult> RunAsync(
        PerformanceFanValidationTarget target,
        CancellationToken cancellationToken) => RunCoreAsync(
            target,
            authorizeTransition: null,
            powerSourceBoundary: null,
            cancellationToken);

    public Task<PerformanceFanSetterValidationResult> RunAsync(
        Func<CancellationToken, Task<HyperBoostAdapterTransitionAuthorization>>
            authorizeTransition,
        CancellationToken cancellationToken) => RunCoreAsync(
            PerformanceFanValidationTarget.HyperBoost,
            authorizeTransition ?? throw new ArgumentNullException(nameof(authorizeTransition)),
            powerSourceBoundary: null,
            cancellationToken);

    public Task<PerformanceFanSetterValidationResult> RunAsync(
        OpenBlade.Contracts.AdapterPowerState destinationAdapterState,
        Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>
            authorizeTransition,
        Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>
            authorizeReconnect,
        CancellationToken cancellationToken) => RunCoreAsync(
            PerformanceFanValidationTarget.HyperBoost,
            authorizeTransition: null,
            new HyperBoostPowerSourceBoundaryRunRequest(
                destinationAdapterState,
                authorizeTransition
                    ?? throw new ArgumentNullException(nameof(authorizeTransition)),
                authorizeReconnect
                    ?? throw new ArgumentNullException(nameof(authorizeReconnect))),
            cancellationToken);

    private async Task<PerformanceFanSetterValidationResult> RunCoreAsync(
        PerformanceFanValidationTarget target,
        Func<CancellationToken, Task<HyperBoostAdapterTransitionAuthorization>>?
            authorizeTransition,
        HyperBoostPowerSourceBoundaryRunRequest? powerSourceBoundary,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(target) || Interlocked.Exchange(ref started, 1) != 0)
        {
            return Empty(target, PerformanceFanValidationFailure.OpenFailed);
        }

        if ((adapterTransitionPlan is not null) != (authorizeTransition is not null)
            || (powerSourceBoundaryPlan is not null) != (powerSourceBoundary is not null)
            || sustainedPlan is not null
            && target != PerformanceFanValidationTarget.HyperBoost)
        {
            return Empty(target, PerformanceFanValidationFailure.WrongTarget);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Empty(target, PerformanceFanValidationFailure.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
        }
        catch
        {
            return Empty(target, PerformanceFanValidationFailure.OpenFailed);
        }

        ValidationActor? actor = null;
        var state = new RunState(target);
        try
        {
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                state.Failure = MapOpenFailure(opened.Failure);
                return state.ToResult();
            }

            PairRead baseline = await ReadPairAsync(
                context, actor.Session, cancellationToken).ConfigureAwait(false);
            if (!baseline.Success)
            {
                state.Failure = baseline.PairMismatch
                    ? PerformanceFanValidationFailure.BaselinePairMismatch
                    : MapCancellation(
                        baseline.Failure,
                        cancellationToken,
                        PerformanceFanValidationFailure.BaselineReadFailed);
                return state.ToResult();
            }

            state.Baseline = baseline.Pair;
            if (baseline.State == ThermalModeState.FixedMaximum)
            {
                state.Failure =
                    PerformanceFanValidationFailure.BaselineFixedSpeedCannotBeRestored;
                return state.ToResult();
            }

            if (IsHyperBoostTarget(target)
                && baseline.State != RequiredHyperBoostBaseline(target))
            {
                state.Failure = target == PerformanceFanValidationTarget.HyperBoost
                    ? PerformanceFanValidationFailure.BaselineNotBalanced
                    : PerformanceFanValidationFailure.BaselineNotRequiredBaseMode;
                return state.ToResult();
            }

            if (baseline.State == ExpectedState(target))
            {
                state.Failure = PerformanceFanValidationFailure.TargetAlreadyActive;
                return state.ToResult();
            }

            if (safetyGate is not null)
            {
                PairRead confirmedBaseline = await ReadPairAsync(
                    context,
                    actor.Session,
                    cancellationToken).ConfigureAwait(false);
                if (!confirmedBaseline.Success)
                {
                    state.Failure = confirmedBaseline.PairMismatch
                        ? PerformanceFanValidationFailure.BaselinePairMismatch
                        : MapCancellation(
                            confirmedBaseline.Failure,
                            cancellationToken,
                            PerformanceFanValidationFailure.BaselineReadFailed);
                    return state.ToResult();
                }

                if (confirmedBaseline.State != RequiredHyperBoostBaseline(target)
                    || confirmedBaseline.Pair != baseline.Pair)
                {
                    state.Failure = PerformanceFanValidationFailure.BaselinePairMismatch;
                    return state.ToResult();
                }

                state.BaselineRequiredBaseModeConfirmed = true;
                state.BaselineBalancedConfirmed =
                    confirmedBaseline.State == ThermalModeState.BalancedAutomatic;

                PerformanceFanValidationSafetyGateResult safety =
                    await safetyGate.ValidateAsync(
                        context,
                        actor.Session,
                        cancellationToken).ConfigureAwait(false);
                state.FullPowerAdapterConfirmed = safety.FullPowerAdapterConfirmed;
                state.InitialAdapterPowerState = safety.AdapterPowerState;
                state.CoolingPadAbsentConfirmed = safety.CoolingPadAbsentConfirmed;
                state.CoolingPadPresentConfirmed = safety.CoolingPadPresentConfirmed;
                state.CoolingPadOpaqueZeroConfirmed =
                    safety.CoolingPadOpaqueZeroConfirmed;
                state.SafetyFailure = safety.Failure;
                if (!safety.Success)
                {
                    state.Failure = safety.Failure ==
                        PerformanceFanValidationSafetyFailure.Canceled
                            ? PerformanceFanValidationFailure.Canceled
                            : PerformanceFanValidationFailure.OpenFailed;
                    return state.ToResult();
                }
            }

            if (!await ObserveExactStageAsync(ExactHyperBoostValidationStage.Baseline,
                context, actor.Session, state, cancellationToken).ConfigureAwait(false))
            {
                return state.ToResult();
            }

            if (padlessNoLoad is not null && (target != PerformanceFanValidationTarget.HyperBoostFromPerformance
                || actor.Session is not Rz090528PadlessHyperBoostValidation.GuardedSession baselineSession
                || !await padlessNoLoad.PrepareAsync(baselineSession, cancellationToken).ConfigureAwait(false)))
            {
                state.Failure = PerformanceFanValidationFailure.BaselineReadFailed;
                return state.ToResult();
            }
            state.ApplyAttempted = true;
            IReadOnlyList<byte[]> applyPayloads = CreateApplyPayloads(target);
            for (int index = 0; index < applyPayloads.Count; index++)
            {
                byte[] payload = applyPayloads[index];
                HidSessionResult<ReadOnlyMemory<byte>> write = await ExecuteAsync(
                    context,
                    actor.Session,
                    CreateEchoCommand(payload),
                    cancellationToken).ConfigureAwait(false);
                if (write.Failure is not null)
                {
                    state.IndeterminatePartialWrite = true;
                    state.ManualRecoveryRequired = true;
                    state.Failure = MapCancellation(
                        write.Failure,
                        cancellationToken,
                        PerformanceFanValidationFailure.ApplyWriteIndeterminate);
                    return state.ToResult();
                }

                if (IsHyperBoostTarget(target)
                    && IsBalancedAutomaticPayload(payload))
                {
                    state.BalancedIntermediaryObserved = true;
                }

                TimeSpan interCommandDelay = FixedInterCommandDelay(target, index);
                if (interCommandDelay > TimeSpan.Zero)
                {
                    await Task.Delay(
                        interCommandDelay,
                        timeProvider,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            state.ApplyAcknowledged = true;
            PairRead candidate = await ReadPairAsync(
                context,
                actor.Session,
                cancellationToken,
                allowHyperBoost: IsHyperBoostTarget(target))
                .ConfigureAwait(false);
            if (!candidate.Success)
            {
                state.IndeterminatePartialWrite = true;
                state.ManualRecoveryRequired = true;
                state.Failure = candidate.PairMismatch
                    ? PerformanceFanValidationFailure.CandidatePairMismatch
                    : MapCancellation(
                        candidate.Failure,
                        cancellationToken,
                        PerformanceFanValidationFailure.CandidateReadFailed);
                return state.ToResult();
            }

            state.Candidate = candidate.Pair;
            state.CandidateConfirmed = candidate.State == ExpectedState(target);
            if (!state.CandidateConfirmed)
            {
                state.Failure = PerformanceFanValidationFailure.CandidateTargetMismatch;
            }
            else if (target == PerformanceFanValidationTarget.Fixed5400)
            {
                state.Failure = await ValidateFixedFanRpmAsync(
                    context,
                    actor.Session,
                    state,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (authorizeTransition is not null)
            {
                state.AdapterTransitionValidation =
                    await ValidateAdapterTransitionAsync(
                        context,
                        actor.Session,
                        adapterTransitionPlan!,
                        state.InitialAdapterPowerState,
                        authorizeTransition,
                        cancellationToken).ConfigureAwait(false);
                if (!state.AdapterTransitionValidation.Success)
                {
                    state.Failure = state.AdapterTransitionValidation.Failure ==
                        HyperBoostAdapterTransitionValidationFailure.Canceled
                            ? PerformanceFanValidationFailure.Canceled
                            : PerformanceFanValidationFailure
                                .SustainedValidationFailed;
                }
            }
            else if (powerSourceBoundary is not null)
            {
                state.PowerSourceBoundaryValidation =
                    await ValidatePowerSourceBoundaryAsync(
                        context,
                        actor.Session,
                        powerSourceBoundaryPlan!,
                        state.InitialAdapterPowerState,
                        baseline,
                        powerSourceBoundary,
                        cancellationToken).ConfigureAwait(false);
                if (!state.PowerSourceBoundaryValidation.ObservationSucceeded)
                {
                    state.Failure = state.PowerSourceBoundaryValidation.Failure ==
                        HyperBoostPowerSourceBoundaryValidationFailure.Canceled
                            ? PerformanceFanValidationFailure.Canceled
                            : PerformanceFanValidationFailure
                                .SustainedValidationFailed;
                }

                if (!state.PowerSourceBoundaryValidation.FullPowerReconnectConfirmed)
                {
                    state.ManualRecoveryRequired = true;
                    return state.ToResult();
                }

                if (state.PowerSourceBoundaryValidation
                    .BaselineReturnedWithoutCleanupWrite)
                {
                    state.Restoration = baseline.Pair;
                    state.Restored = true;
                }
            }
            else if (target == PerformanceFanValidationTarget.HyperBoost
                && sustainedPlan is not null
                && thermalTelemetrySource is not null)
            {
                state.SustainedValidation = await ValidateSustainedHyperBoostAsync(
                    context,
                    actor.Session,
                    sustainedPlan,
                    thermalTelemetrySource,
                    externalWorkloadSource,
                    cancellationToken).ConfigureAwait(false);
                if (!state.SustainedValidation.Success)
                {
                    state.Failure = state.SustainedValidation.Failure ==
                        HyperBoostSustainedValidationFailure.Canceled
                            ? PerformanceFanValidationFailure.Canceled
                            : PerformanceFanValidationFailure.SustainedValidationFailed;
                }
            }

            if (exactObserver is not null && (!state.CandidateConfirmed ||
                !await ObserveExactStageAsync(ExactHyperBoostValidationStage.Candidate,
                    context, actor.Session, state, cancellationToken).ConfigureAwait(false)))
            {
                state.ManualRecoveryRequired = true;
                return state.ToResult();
            }

            if (padlessNoLoad is not null)
            {
                if (!state.CandidateConfirmed || actor.Session is not Rz090528PadlessHyperBoostValidation.GuardedSession dwellSession)
                {
                    state.ManualRecoveryRequired = true;
                    return state.ToResult();
                }
                Rz090528NoLoadResult observed = await padlessNoLoad.RunAsync(dwellSession, cancellationToken).ConfigureAwait(false);
                if (!observed.Completed)
                {
                    state.Failure = PerformanceFanValidationFailure.SustainedValidationFailed;
                }

                if (!observed.PermitKnownStateRestore)
                {
                    state.ManualRecoveryRequired = true;
                    return state.ToResult();
                }
            }

            // Restoration starts only after both candidate channels have been read
            // successfully. A failed or mismatched paired read is indeterminate and
            // deliberately requires manual recovery instead of another write.
            using CancellationTokenSource cleanup = new(cleanupDrainTimeout);
            if (!state.Restored)
            {
                state.RestoreAttempted = true;
                foreach (byte[] payload in CreateAutomaticStatePayloads(
                    baseline.State!.Value))
                {
                    state.RestoreWriteCount++;
                    HidSessionResult<ReadOnlyMemory<byte>> write = await ExecuteAsync(
                        context,
                        actor.Session,
                        CreateEchoCommand(payload),
                        cleanup.Token).ConfigureAwait(false);
                    if (write.Failure is not null)
                    {
                        state.IndeterminatePartialWrite = true;
                        state.ManualRecoveryRequired = true;
                        state.Failure =
                            PerformanceFanValidationFailure.RestoreWriteIndeterminate;
                        return state.ToResult();
                    }
                }

                state.RestoreAcknowledged = true;
                PairRead restoration = await ReadPairAsync(
                    context, actor.Session, cleanup.Token).ConfigureAwait(false);
                if (!restoration.Success)
                {
                    state.ManualRecoveryRequired = true;
                    state.Failure = restoration.PairMismatch
                        ? PerformanceFanValidationFailure.RestorationPairMismatch
                        : PerformanceFanValidationFailure.RestorationReadFailed;
                    return state.ToResult();
                }

                state.Restoration = restoration.Pair;
                state.Restored = restoration.Pair == baseline.Pair;
                if (!state.Restored)
                {
                    state.ManualRecoveryRequired = true;
                    state.Failure = PerformanceFanValidationFailure.RestorationMismatch;
                }
            }

            if (state.Restored && !await ObserveExactStageAsync(
                ExactHyperBoostValidationStage.Restored, context, actor.Session,
                state, cancellationToken).ConfigureAwait(false))
            {
                return state.ToResult();
            }

            if (state.Restored
                && sustainedPlan is not null
                && safetyGate is not null
                && authorizeTransition is null)
            {
                PerformanceFanValidationSafetyGateResult postRestorationSafety =
                    await safetyGate.ValidateAsync(
                        context,
                        actor.Session,
                        cleanup.Token).ConfigureAwait(false);
                state.PostRestorationFullPowerAdapterConfirmed =
                    postRestorationSafety.FullPowerAdapterConfirmed;
                state.PostRestorationCoolingPadAbsentConfirmed =
                    postRestorationSafety.CoolingPadAbsentConfirmed;
                state.PostRestorationSafetyFailure =
                    postRestorationSafety.Failure;
                if (!postRestorationSafety.Success)
                {
                    state.Failure =
                        PerformanceFanValidationFailure.SustainedValidationFailed;
                }
            }
            else if (state.Restored && !state.CandidateConfirmed)
            {
                state.Failure = PerformanceFanValidationFailure.CandidateTargetMismatch;
            }
            else if (state.Restored
                && state.Failure == PerformanceFanValidationFailure.None)
            {
                state.Failure = PerformanceFanValidationFailure.None;
            }

            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Failure = PerformanceFanValidationFailure.Canceled;
            if (state.ApplyAttempted && !state.Restored)
            {
                state.IndeterminatePartialWrite = true;
                state.ManualRecoveryRequired = true;
            }

            return state.ToResult();
        }
        catch
        {
            state.Failure = state.ApplyAttempted
                ? PerformanceFanValidationFailure.ApplyWriteIndeterminate
                : PerformanceFanValidationFailure.OpenFailed;
            if (state.ApplyAttempted)
            {
                state.IndeterminatePartialWrite = true;
                state.ManualRecoveryRequired = true;
            }

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

    private async Task<bool> ObserveExactStageAsync(
        ExactHyperBoostValidationStage stage, ValidationRunContext context,
        IRazerCommandSession session, RunState state, CancellationToken token)
    {
        if (exactObserver is null)
        {
            return true;
        }

        async Task<bool> Guard(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (session is not Rz090528HyperBoostValidation.GuardedSession exact
                || !await exact.ValidateExternalWriteAsync(context, exchangeTimeout, cancellation).ConfigureAwait(false))
            {
                return false;
            }

            PairRead pair = await ReadPairAsync(context, session, cancellation,
                allowHyperBoost: stage == ExactHyperBoostValidationStage.Candidate).ConfigureAwait(false);
            return pair.Success && pair.State == (stage == ExactHyperBoostValidationStage.Candidate
                ? ThermalModeState.HyperBoostObserved : ThermalModeState.PerformanceAutomatic);
        }
        try
        {
            if (state.TargetForObserver != PerformanceFanValidationTarget.HyperBoostFromPerformance
                || !await Guard(token).ConfigureAwait(false)
                || !await exactObserver(stage, Guard, token).ConfigureAwait(false))
            {
                throw new InvalidOperationException();
            }

            return true;
        }
        catch
        {
            state.Failure = token.IsCancellationRequested
                ? PerformanceFanValidationFailure.Canceled
                : PerformanceFanValidationFailure.SustainedValidationFailed;
            state.ManualRecoveryRequired = true;
            // The observer may have changed GPU offsets. Do not attempt cleanup writes.
            try
            {
                PairRead observed = await ReadPairAsync(context, session, CancellationToken.None,
                    allowHyperBoost: true).ConfigureAwait(false);
                state.ObserverFailureReconciliation = observed.Pair;
            }
            catch { }
            return false;
        }
    }

    private async Task<PairRead> ReadPairAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken,
        bool allowHyperBoost = false)
    {
        byte[][] responses = new byte[2][];
        for (byte thermalId = 1; thermalId <= 2; thermalId++)
        {
            byte[] request = [0x01, thermalId, 0x00, 0x00];
            ThermalChannel channel = (ThermalChannel)thermalId;
            var command = new RazerCommandSpec(
                CommandClass,
                QueryCommandId,
                request,
                4,
                payload => ThermalModeProtocol.TryDecodeQuery(
                    payload.Span,
                    channel,
                    out _));
            HidSessionResult<ReadOnlyMemory<byte>> response = await ExecuteAsync(
                context, session, command, cancellationToken).ConfigureAwait(false);
            if (response.Failure is not null)
            {
                return new(null, null, null, null, response.Failure, false);
            }

            responses[thermalId - 1] = response.Value.ToArray();
        }

        bool pairMatches = ThermalModeProtocol.TryDecodeMatchingPair(
            responses[0],
            responses[1],
            out ThermalModeState state);
        return pairMatches && IsAdmittedValidationState(state, allowHyperBoost)
            ? new(
                responses[0],
                responses[1],
                new(Convert.ToHexString(responses[0]), Convert.ToHexString(responses[1])),
                state,
                null,
                false)
            : new(responses[0], responses[1], null, null, null, true);
    }

    private static bool IsAdmittedValidationState(
        ThermalModeState state,
        bool allowHyperBoost) => state is
            ThermalModeState.BalancedAutomatic
            or ThermalModeState.SilentAutomatic
            or ThermalModeState.PerformanceAutomatic
            or ThermalModeState.FixedMaximum
            or ThermalModeState.BatterySaverAutomatic
            or ThermalModeState.BatteryBalancedAutomatic
        || allowHyperBoost && state == ThermalModeState.HyperBoostObserved;

    private async Task<PerformanceFanValidationFailure> ValidateFixedFanRpmAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RunState state,
        CancellationToken cancellationToken)
    {
        try
        {
            int consecutiveHighSamples = 0;
            for (int sampleNumber = 1;
                 sampleNumber <= fixedFanMaximumSamples;
                 sampleNumber++)
            {
                await Task.Delay(
                    fixedFanSampleInterval,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);

                FanRpmReadings? sample = await ReadFanRpmSampleAsync(
                    context,
                    session,
                    cancellationToken).ConfigureAwait(false);
                if (sample is null)
                {
                    return cancellationToken.IsCancellationRequested
                        ? PerformanceFanValidationFailure.Canceled
                        : PerformanceFanValidationFailure.FixedFanRpmReadFailed;
                }

                state.CandidateFanRpm = sample;
                state.CandidateFanRpmSamples.Add(sample);
                consecutiveHighSamples = IsFixedTargetRpm(sample.Selector1Rpm)
                    && IsFixedTargetRpm(sample.Selector2Rpm)
                        ? consecutiveHighSamples + 1
                        : 0;

                bool successThresholdReached =
                    consecutiveHighSamples >= FixedFanRequiredConsecutiveSamples;
                if (sampleNumber % fixedFanStateCheckEverySamples == 0
                    || successThresholdReached)
                {
                    PerformanceFanValidationFailure stateFailure =
                        await ValidateFixedFanStateAsync(
                            context,
                            session,
                            cancellationToken).ConfigureAwait(false);
                    if (stateFailure != PerformanceFanValidationFailure.None)
                    {
                        return stateFailure;
                    }
                }

                if (successThresholdReached)
                {
                    return PerformanceFanValidationFailure.None;
                }
            }

            return PerformanceFanValidationFailure.FixedFanRpmOutOfRange;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PerformanceFanValidationFailure.Canceled;
        }
        catch
        {
            return PerformanceFanValidationFailure.FixedFanRpmReadFailed;
        }
    }

    private async Task<HyperBoostSustainedValidationResult>
        ValidateSustainedHyperBoostAsync(
            ValidationRunContext context,
            IRazerCommandSession session,
            ReviewedHyperBoostThermalPlan plan,
            IHyperBoostThermalTelemetrySource telemetrySource,
            IHyperBoostExternalWorkloadSource? workloadSource,
            CancellationToken cancellationToken)
    {
        int plannedSampleCount = checked((int)(plan.Duration.Ticks /
            plan.SampleInterval.Ticks));
        var samples = new List<HyperBoostThermalValidationSample>(
            plannedSampleCount);
        long startedAt = timeProvider.GetTimestamp();
        bool continuousPadAbsence = true;
        bool fullPowerMaintained = true;
        bool hyperBoostMaintained = true;
        bool requiredSensorsMaintained = true;
        bool externalWorkloadMaintained =
            plan.WorkloadKind == HyperBoostValidationWorkloadKind.SustainedNoLoadDwell;

        HyperBoostSustainedValidationResult Result(
            HyperBoostSustainedValidationFailure failure) => new(
                plan.PlanId,
                plan.SourceSha256,
                plannedSampleCount,
                samples.Count,
                failure == HyperBoostSustainedValidationFailure.None
                    && samples.Count == plannedSampleCount,
                continuousPadAbsence,
                fullPowerMaintained,
                hyperBoostMaintained,
                requiredSensorsMaintained,
                externalWorkloadMaintained,
                samples.ToArray(),
                failure);

        async ValueTask<HyperBoostSustainedValidationFailure?> ObserveWorkloadAsync()
        {
            if (plan.WorkloadKind != HyperBoostValidationWorkloadKind.ExternalReviewedLoad)
            {
                return null;
            }

            HyperBoostExternalWorkloadObservation observation;
            try
            {
                observation = await workloadSource!.ObserveAsync(plan, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return HyperBoostSustainedValidationFailure.Canceled;
            }
            catch
            {
                observation = HyperBoostExternalWorkloadObservation.ObservationFailed;
            }

            if (observation == HyperBoostExternalWorkloadObservation.RunningExact)
            {
                externalWorkloadMaintained = true;
                return null;
            }

            externalWorkloadMaintained = false;
            return observation switch
            {
                HyperBoostExternalWorkloadObservation.NotRunning =>
                    HyperBoostSustainedValidationFailure.ExternalWorkloadNotRunning,
                HyperBoostExternalWorkloadObservation.IdentityMismatch =>
                    HyperBoostSustainedValidationFailure.ExternalWorkloadIdentityMismatch,
                _ => HyperBoostSustainedValidationFailure
                    .ExternalWorkloadObservationFailed,
            };
        }

        try
        {
            HyperBoostSustainedValidationFailure? initialWorkloadFailure =
                await ObserveWorkloadAsync().ConfigureAwait(false);
            if (initialWorkloadFailure is { } initialFailure)
            {
                return Result(initialFailure);
            }

            for (int sampleNumber = 1;
                 sampleNumber <= plannedSampleCount;
                 sampleNumber++)
            {
                await Task.Delay(
                    plan.SampleInterval,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);

                HyperBoostSustainedValidationFailure? workloadFailure =
                    await ObserveWorkloadAsync().ConfigureAwait(false);
                if (workloadFailure is { } failedWorkload)
                {
                    return Result(failedWorkload);
                }

                PerformanceFanValidationSafetyGateResult safety =
                    await safetyGate!.ValidateAsync(
                        context,
                        session,
                        cancellationToken).ConfigureAwait(false);
                if (!safety.Success)
                {
                    HyperBoostSustainedValidationFailure failure =
                        MapSustainedSafetyFailure(safety.Failure);
                    if (failure is HyperBoostSustainedValidationFailure.CoolingPadPresent
                        or HyperBoostSustainedValidationFailure.CoolingPadAbsenceObservationFailed)
                    {
                        continuousPadAbsence = false;
                    }
                    else if (failure is HyperBoostSustainedValidationFailure.AdapterPowerReadFailed
                        or HyperBoostSustainedValidationFailure.FullPowerAdapterRequired)
                    {
                        fullPowerMaintained = false;
                    }

                    return Result(failure);
                }

                PairRead thermalState = await ReadPairAsync(
                    context,
                    session,
                    cancellationToken,
                    allowHyperBoost: true).ConfigureAwait(false);
                if (!thermalState.Success)
                {
                    hyperBoostMaintained = false;
                    return Result(thermalState.PairMismatch
                        ? HyperBoostSustainedValidationFailure.ThermalStatePairMismatch
                        : HyperBoostSustainedValidationFailure.ThermalStateReadFailed);
                }

                if (thermalState.State != ThermalModeState.HyperBoostObserved)
                {
                    hyperBoostMaintained = false;
                    return Result(
                        HyperBoostSustainedValidationFailure.HyperBoostStateLost);
                }

                HyperBoostTemperatureObservation temperatures =
                    await telemetrySource.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (temperatures.CpuCelsius is not int cpu)
                {
                    requiredSensorsMaintained = false;
                    return Result(
                        HyperBoostSustainedValidationFailure.CpuTemperatureUnavailable);
                }

                bool gpuUnavailable = temperatures.GpuCelsius is null
                    && !(temperatures.GpuInactive
                        && plan.GpuSensorPolicy ==
                            HyperBoostGpuSensorPolicy.AllowInactive);
                if (gpuUnavailable)
                {
                    requiredSensorsMaintained = false;
                    return Result(
                        HyperBoostSustainedValidationFailure.GpuTemperatureUnavailable);
                }

                FanRpmReadings? fanRpm = await ReadFanRpmSampleAsync(
                    context,
                    session,
                    cancellationToken).ConfigureAwait(false);
                if (fanRpm is null)
                {
                    return Result(
                        HyperBoostSustainedValidationFailure.FanRpmReadFailed);
                }

                samples.Add(new(
                    sampleNumber,
                    timeProvider.GetElapsedTime(startedAt),
                    cpu,
                    temperatures.GpuCelsius,
                    temperatures.GpuInactive,
                    fanRpm.Selector1Rpm,
                    fanRpm.Selector2Rpm));

                if (cpu > plan.MaximumCpuTemperatureCelsius)
                {
                    requiredSensorsMaintained = false;
                    return Result(
                        HyperBoostSustainedValidationFailure.CpuTemperatureExceeded);
                }

                if (temperatures.GpuCelsius is int gpu
                    && gpu > plan.MaximumGpuTemperatureCelsius)
                {
                    requiredSensorsMaintained = false;
                    return Result(
                        HyperBoostSustainedValidationFailure.GpuTemperatureExceeded);
                }

                if (plan.MinimumFan1Rpm is int minimumFan1
                    && fanRpm.Selector1Rpm < minimumFan1)
                {
                    return Result(
                        HyperBoostSustainedValidationFailure.Fan1BelowMinimum);
                }

                if (plan.MinimumFan2Rpm is int minimumFan2
                    && fanRpm.Selector2Rpm < minimumFan2)
                {
                    return Result(
                        HyperBoostSustainedValidationFailure.Fan2BelowMinimum);
                }
            }

            return Result(HyperBoostSustainedValidationFailure.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(HyperBoostSustainedValidationFailure.Canceled);
        }
        catch
        {
            return Result(HyperBoostSustainedValidationFailure.UnexpectedFailure);
        }
    }

    private async Task<HyperBoostAdapterTransitionValidationResult>
        ValidateAdapterTransitionAsync(
            ValidationRunContext context,
            IRazerCommandSession session,
            ReviewedHyperBoostAdapterTransitionPlan plan,
            OpenBlade.Contracts.AdapterPowerState initialAdapterState,
            Func<CancellationToken, Task<HyperBoostAdapterTransitionAuthorization>>
                authorizeTransition,
            CancellationToken cancellationToken)
    {
        HyperBoostAdapterTransitionValidationResult Result(
            bool promptConfirmed,
            bool padAbsentConfirmed,
            bool readCompleted,
            OpenBlade.Contracts.AdapterPowerState observed,
            HyperBoostAdapterTransitionValidationFailure failure) => new(
                plan.PlanId,
                plan.SourceSha256,
                promptConfirmed,
                padAbsentConfirmed,
                readCompleted,
                initialAdapterState,
                observed,
                TransitionConfirmed:
                    failure == HyperBoostAdapterTransitionValidationFailure.None,
                failure);

        try
        {
            HyperBoostAdapterTransitionAuthorization authorization =
                await authorizeTransition(cancellationToken).ConfigureAwait(false);
            if (authorization != HyperBoostAdapterTransitionAuthorization.PadAbsentConfirmed)
            {
                return Result(
                    promptConfirmed:
                        authorization != HyperBoostAdapterTransitionAuthorization.Rejected,
                    padAbsentConfirmed: false,
                    readCompleted: false,
                    OpenBlade.Contracts.AdapterPowerState.Unknown,
                    authorization switch
                    {
                        HyperBoostAdapterTransitionAuthorization.CoolingPadPresent =>
                            HyperBoostAdapterTransitionValidationFailure.CoolingPadPresent,
                        HyperBoostAdapterTransitionAuthorization
                            .CoolingPadAbsenceObservationFailed =>
                            HyperBoostAdapterTransitionValidationFailure
                                .CoolingPadAbsenceObservationFailed,
                        _ => HyperBoostAdapterTransitionValidationFailure
                            .TransitionNotConfirmed,
                    });
            }

            if (!plan.InitialAdapterStates.Contains(initialAdapterState))
            {
                return Result(
                    promptConfirmed: true,
                    padAbsentConfirmed: true,
                    readCompleted: false,
                    OpenBlade.Contracts.AdapterPowerState.Unknown,
                    HyperBoostAdapterTransitionValidationFailure
                        .ReviewedNonFullPowerNotObserved);
            }

            if (!context.TryAllocateTransaction(out byte transactionId))
            {
                return Result(
                    promptConfirmed: true,
                    padAbsentConfirmed: true,
                    readCompleted: false,
                    OpenBlade.Contracts.AdapterPowerState.Unknown,
                    HyperBoostAdapterTransitionValidationFailure
                        .AdapterPowerReadFailed);
            }

            var adapter = new AdapterPowerCommandClient(session, exchangeTimeout);
            HidSessionResult<OpenBlade.Contracts.AdapterPowerState> observation =
                await adapter.QueryAsync(transactionId, cancellationToken)
                    .ConfigureAwait(false);
            if (observation.Failure is not null)
            {
                return Result(
                    promptConfirmed: true,
                    padAbsentConfirmed: true,
                    readCompleted: false,
                    OpenBlade.Contracts.AdapterPowerState.Unknown,
                    cancellationToken.IsCancellationRequested
                        ? HyperBoostAdapterTransitionValidationFailure.Canceled
                        : HyperBoostAdapterTransitionValidationFailure
                            .AdapterPowerReadFailed);
            }

            return observation.Value switch
            {
                var value when value == plan.DestinationAdapterState => Result(
                    promptConfirmed: true,
                    padAbsentConfirmed: true,
                    readCompleted: true,
                    observation.Value,
                    HyperBoostAdapterTransitionValidationFailure.None),
                OpenBlade.Contracts.AdapterPowerState.Unknown => Result(
                    promptConfirmed: true,
                    padAbsentConfirmed: true,
                    readCompleted: true,
                    observation.Value,
                    HyperBoostAdapterTransitionValidationFailure
                        .AdapterPowerUnknown),
                _ => Result(
                    promptConfirmed: true,
                    padAbsentConfirmed: true,
                    readCompleted: true,
                    observation.Value,
                    HyperBoostAdapterTransitionValidationFailure
                        .ReviewedNonFullPowerNotObserved),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                promptConfirmed: false,
                padAbsentConfirmed: false,
                readCompleted: false,
                OpenBlade.Contracts.AdapterPowerState.Unknown,
                HyperBoostAdapterTransitionValidationFailure.Canceled);
        }
        catch
        {
            return Result(
                promptConfirmed: false,
                padAbsentConfirmed: false,
                readCompleted: false,
                OpenBlade.Contracts.AdapterPowerState.Unknown,
                HyperBoostAdapterTransitionValidationFailure.UnexpectedFailure);
        }
    }

    private async Task<HyperBoostPowerSourceBoundaryValidationResult>
        ValidatePowerSourceBoundaryAsync(
            ValidationRunContext context,
            IRazerCommandSession session,
            ReviewedHyperBoostPowerSourceBoundaryPlan plan,
            OpenBlade.Contracts.AdapterPowerState initialAdapterState,
            PairRead baseline,
            HyperBoostPowerSourceBoundaryRunRequest request,
            CancellationToken cancellationToken)
    {
        int maximumSampleCount = checked((int)(plan.ObservationDuration.Ticks /
            plan.SampleInterval.Ticks));
        var samples = new List<HyperBoostPowerSourceBoundarySample>(
            maximumSampleCount);
        bool transitionPromptConfirmed = false;
        bool transitionPadAbsent = false;
        bool destinationObserved = false;
        bool destinationMaintained = true;
        bool completedFullObservation = false;
        bool firmwareExitConfirmed = false;
        bool hyperBoostReappearedAfterExit = false;
        ThermalModeState? firmwareExitState = null;
        bool reconnectPromptConfirmed = false;
        bool reconnectPadAbsent = false;
        bool fullPowerReconnectConfirmed = false;
        OpenBlade.Contracts.AdapterPowerState reconnectedAdapterState =
            OpenBlade.Contracts.AdapterPowerState.Unknown;
        bool baselineReturnedWithoutCleanupWrite = false;
        HyperBoostPowerSourceBoundaryValidationFailure failure =
            HyperBoostPowerSourceBoundaryValidationFailure.None;
        long startedAt = timeProvider.GetTimestamp();

        HyperBoostPowerSourceBoundaryValidationResult Result() => new(
            plan.PlanId,
            plan.SourceSha256,
            request.DestinationAdapterState,
            transitionPromptConfirmed,
            transitionPadAbsent,
            destinationObserved,
            destinationMaintained,
            completedFullObservation,
            firmwareExitConfirmed,
            hyperBoostReappearedAfterExit,
            firmwareExitState,
            plan.RequiredConsecutiveAutomaticSamples,
            maximumSampleCount,
            samples.ToArray(),
            OpenBladeDisableIssuedDuringObservation: false,
            reconnectPromptConfirmed,
            reconnectPadAbsent,
            fullPowerReconnectConfirmed,
            reconnectedAdapterState,
            baselineReturnedWithoutCleanupWrite,
            OpenBladeCleanupWriteCount: 0,
            failure);

        try
        {
            if (!plan.InitialAdapterStates.Contains(initialAdapterState)
                || !plan.DestinationAdapterStates.Contains(
                    request.DestinationAdapterState))
            {
                failure = HyperBoostPowerSourceBoundaryValidationFailure
                    .DestinationNotReviewed;
                return Result();
            }

            HyperBoostPowerSourceBoundaryAuthorization transition =
                await request.AuthorizeTransition(cancellationToken)
                    .ConfigureAwait(false);
            transitionPromptConfirmed = transition.Confirmed;
            transitionPadAbsent = transition.CoolingPadObservation ==
                CoolingPadPhysicalPresenceObservation.Absent;
            if (!transitionPromptConfirmed)
            {
                failure = HyperBoostPowerSourceBoundaryValidationFailure
                    .TransitionNotConfirmed;
            }
            else if (!transitionPadAbsent)
            {
                failure = transition.CoolingPadObservation ==
                    CoolingPadPhysicalPresenceObservation.Present
                        ? HyperBoostPowerSourceBoundaryValidationFailure
                            .CoolingPadPresent
                        : HyperBoostPowerSourceBoundaryValidationFailure
                            .CoolingPadAbsenceObservationFailed;
            }
            else
            {
                int consecutiveAutomaticSamples = 0;
                for (int sampleNumber = 1;
                     sampleNumber <= maximumSampleCount;
                     sampleNumber++)
                {
                    await Task.Delay(
                        plan.SampleInterval,
                        timeProvider,
                        cancellationToken).ConfigureAwait(false);
                    HidSessionResult<OpenBlade.Contracts.AdapterPowerState> adapter =
                        await ReadAdapterPowerAsync(
                            context,
                            session,
                            cancellationToken).ConfigureAwait(false);
                    if (adapter.Failure is not null)
                    {
                        failure = cancellationToken.IsCancellationRequested
                            ? HyperBoostPowerSourceBoundaryValidationFailure.Canceled
                            : HyperBoostPowerSourceBoundaryValidationFailure
                                .AdapterPowerReadFailed;
                        break;
                    }

                    PairRead pair = await ReadPairAsync(
                        context,
                        session,
                        cancellationToken,
                        allowHyperBoost: true).ConfigureAwait(false);
                    if (!pair.Success)
                    {
                        failure = pair.PairMismatch
                            ? HyperBoostPowerSourceBoundaryValidationFailure
                                .ThermalStatePairMismatch
                            : cancellationToken.IsCancellationRequested
                                ? HyperBoostPowerSourceBoundaryValidationFailure.Canceled
                                : HyperBoostPowerSourceBoundaryValidationFailure
                                    .ThermalStateReadFailed;
                        break;
                    }

                    ThermalModeState state = pair.State!.Value;
                    samples.Add(new(
                        sampleNumber,
                        timeProvider.GetElapsedTime(startedAt),
                        adapter.Value,
                        state));
                    bool atDestination =
                        adapter.Value == request.DestinationAdapterState;
                    destinationObserved |= atDestination;
                    if (!atDestination)
                    {
                        destinationMaintained = false;
                        failure = HyperBoostPowerSourceBoundaryValidationFailure
                            .DestinationPowerNotObserved;
                        break;
                    }

                    if (IsAutomaticNonHyperBoostState(state))
                    {
                        consecutiveAutomaticSamples++;
                    }
                    else if (state == ThermalModeState.HyperBoostObserved)
                    {
                        if (firmwareExitConfirmed)
                        {
                            hyperBoostReappearedAfterExit = true;
                            failure = HyperBoostPowerSourceBoundaryValidationFailure
                                .HyperBoostReappearedAfterExit;
                            break;
                        }

                        consecutiveAutomaticSamples = 0;
                    }
                    else
                    {
                        failure = HyperBoostPowerSourceBoundaryValidationFailure
                            .AutomaticStateNotMaintained;
                        break;
                    }

                    if (consecutiveAutomaticSamples >=
                        plan.RequiredConsecutiveAutomaticSamples)
                    {
                        firmwareExitConfirmed = true;
                        firmwareExitState = state;
                    }

                    if (sampleNumber == maximumSampleCount)
                    {
                        completedFullObservation = true;
                    }
                }

                if (failure == HyperBoostPowerSourceBoundaryValidationFailure.None
                    && !destinationObserved)
                {
                    failure = HyperBoostPowerSourceBoundaryValidationFailure
                        .DestinationPowerNotObserved;
                }
                else if (failure ==
                        HyperBoostPowerSourceBoundaryValidationFailure.None
                    && !firmwareExitConfirmed)
                {
                    failure = HyperBoostPowerSourceBoundaryValidationFailure
                        .HyperBoostDidNotExit;
                }
            }

            if (transitionPromptConfirmed)
            {
                HyperBoostPowerSourceBoundaryAuthorization reconnect =
                    await request.AuthorizeReconnect(cancellationToken)
                        .ConfigureAwait(false);
                reconnectPromptConfirmed = reconnect.Confirmed;
                reconnectPadAbsent = reconnect.CoolingPadObservation ==
                    CoolingPadPhysicalPresenceObservation.Absent;
                if (!reconnectPromptConfirmed)
                {
                    failure = HyperBoostPowerSourceBoundaryValidationFailure
                        .ReconnectNotConfirmed;
                    return Result();
                }

                if (!reconnectPadAbsent
                    && failure ==
                        HyperBoostPowerSourceBoundaryValidationFailure.None)
                {
                    failure = reconnect.CoolingPadObservation ==
                        CoolingPadPhysicalPresenceObservation.Present
                            ? HyperBoostPowerSourceBoundaryValidationFailure
                                .CoolingPadPresent
                            : HyperBoostPowerSourceBoundaryValidationFailure
                                .CoolingPadAbsenceObservationFailed;
                }
            }

            HidSessionResult<OpenBlade.Contracts.AdapterPowerState> reconnectedAdapter =
                await ReadAdapterPowerAsync(
                    context,
                    session,
                    cancellationToken).ConfigureAwait(false);
            if (reconnectedAdapter.Failure is not null)
            {
                failure = cancellationToken.IsCancellationRequested
                    ? HyperBoostPowerSourceBoundaryValidationFailure.Canceled
                    : HyperBoostPowerSourceBoundaryValidationFailure
                        .ReconnectAdapterPowerReadFailed;
                return Result();
            }

            reconnectedAdapterState = reconnectedAdapter.Value;
            fullPowerReconnectConfirmed =
                HyperBoostValidationPower.IsAdmittedFullPower(
                    reconnectedAdapterState);
            if (!fullPowerReconnectConfirmed)
            {
                failure = HyperBoostPowerSourceBoundaryValidationFailure
                    .FullPowerReconnectRequired;
                return Result();
            }

            PairRead postReconnect = await ReadPairAsync(
                context,
                session,
                cancellationToken,
                allowHyperBoost: true).ConfigureAwait(false);
            baselineReturnedWithoutCleanupWrite = postReconnect.Success
                && postReconnect.Pair == baseline.Pair;
            return Result();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = HyperBoostPowerSourceBoundaryValidationFailure.Canceled;
            return Result();
        }
        catch
        {
            failure = HyperBoostPowerSourceBoundaryValidationFailure.UnexpectedFailure;
            return Result();
        }
    }

    private async Task<HidSessionResult<OpenBlade.Contracts.AdapterPowerState>>
        ReadAdapterPowerAsync(
            ValidationRunContext context,
            IRazerCommandSession session,
            CancellationToken cancellationToken)
    {
        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            return new(default, HidSessionFailureKind.Exhausted);
        }

        var adapter = new AdapterPowerCommandClient(session, exchangeTimeout);
        return await adapter.QueryAsync(transactionId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static HyperBoostSustainedValidationFailure MapSustainedSafetyFailure(
        PerformanceFanValidationSafetyFailure failure) => failure switch
        {
            PerformanceFanValidationSafetyFailure.AdapterPowerReadFailed =>
                HyperBoostSustainedValidationFailure.AdapterPowerReadFailed,
            PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired =>
                HyperBoostSustainedValidationFailure.FullPowerAdapterRequired,
            PerformanceFanValidationSafetyFailure.CoolingPadPresent =>
                HyperBoostSustainedValidationFailure.CoolingPadPresent,
            PerformanceFanValidationSafetyFailure.CoolingPadAbsenceObservationFailed =>
                HyperBoostSustainedValidationFailure.CoolingPadAbsenceObservationFailed,
            PerformanceFanValidationSafetyFailure.Canceled =>
                HyperBoostSustainedValidationFailure.Canceled,
            _ => HyperBoostSustainedValidationFailure.UnexpectedFailure,
        };

    private async Task<FanRpmReadings?> ReadFanRpmSampleAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken)
    {
        int[] readings = new int[2];
        for (byte selectorValue = 1; selectorValue <= 2; selectorValue++)
        {
            FanRpmSelector selector = (FanRpmSelector)selectorValue;
            var command = new RazerCommandSpec(
                CommandClass,
                0x88,
                [0x01, selectorValue, 0x00],
                3,
                payload => FanRpmProtocol.TryDecodeQuery(
                    payload.Span,
                    selector,
                    out _));
            HidSessionResult<ReadOnlyMemory<byte>> response = await ExecuteAsync(
                context,
                session,
                command,
                cancellationToken).ConfigureAwait(false);
            if (response.Failure is not null
                || !FanRpmProtocol.TryDecodeQuery(
                    response.Value.Span,
                    selector,
                    out readings[selectorValue - 1]))
            {
                return null;
            }
        }

        return new(readings[0], readings[1]);
    }

    private async Task<PerformanceFanValidationFailure> ValidateFixedFanStateAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken)
    {
        PairRead state = await ReadPairAsync(
            context,
            session,
            cancellationToken).ConfigureAwait(false);
        if (!state.Success)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return PerformanceFanValidationFailure.Canceled;
            }

            return state.PairMismatch
                ? PerformanceFanValidationFailure.CandidatePairMismatch
                : PerformanceFanValidationFailure.CandidateReadFailed;
        }

        return state.State == ThermalModeState.FixedMaximum
            ? PerformanceFanValidationFailure.None
            : PerformanceFanValidationFailure.CandidateTargetMismatch;
    }

    private static bool IsFixedTargetRpm(int rpm) =>
        rpm >= FixedFanMinimumRpm && rpm <= FixedFanMaximumRpm;

    private static bool IsAutomaticNonHyperBoostState(ThermalModeState state) =>
        state is ThermalModeState.BalancedAutomatic
            or ThermalModeState.SilentAutomatic
            or ThermalModeState.PerformanceAutomatic
            or ThermalModeState.BatterySaverAutomatic
            or ThermalModeState.BatteryBalancedAutomatic
            or ThermalModeState.CustomAutomatic;

    private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RazerCommandSpec command,
        CancellationToken cancellationToken)
    {
        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            return new(default, HidSessionFailureKind.Exhausted);
        }

        return await session.ExecuteAsync(
            command,
            () => TransactionIdAssignment.Valid(transactionId),
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static RazerCommandSpec CreateEchoCommand(byte[] payload) => new(
        CommandClass,
        payload.Length == 3 ? SetSpeedCommandId : SetStateCommandId,
        payload,
        payload.Length,
        response => response.Span.SequenceEqual(payload));

    private static IReadOnlyList<byte[]> CreateApplyPayloads(
        PerformanceFanValidationTarget target) => target switch
        {
            PerformanceFanValidationTarget.Balanced =>
                [[0x01, 0x01, 0x00, 0x00], [0x01, 0x02, 0x00, 0x00]],
            PerformanceFanValidationTarget.Silent =>
                [[0x01, 0x01, 0x05, 0x00], [0x01, 0x02, 0x05, 0x00]],
            PerformanceFanValidationTarget.Performance =>
                [[0x01, 0x01, 0x02, 0x00], [0x01, 0x02, 0x02, 0x00]],
            PerformanceFanValidationTarget.BatterySaver =>
                [[0x01, 0x01, 0x03, 0x00], [0x01, 0x02, 0x03, 0x00]],
            PerformanceFanValidationTarget.BatteryBalanced =>
                [[0x01, 0x01, 0x06, 0x00], [0x01, 0x02, 0x06, 0x00]],
            PerformanceFanValidationTarget.HyperBoost =>
                [[0x01, 0x01, 0x07, 0x00], [0x01, 0x02, 0x07, 0x00]],
            PerformanceFanValidationTarget.HyperBoostFromSilent =>
                [[0x01, 0x01, 0x07, 0x00], [0x01, 0x02, 0x07, 0x00]],
            PerformanceFanValidationTarget.HyperBoostFromPerformance =>
                [[0x01, 0x01, 0x07, 0x00], [0x01, 0x02, 0x07, 0x00]],
            // Synapse writes mode 00 + fixed flag 01, then 54 (5400 RPM / 100).
            // The independent 0D/82 query normalizes this to mode 04 + flag 01.
            PerformanceFanValidationTarget.Fixed5400 =>
                [[0x01, 0x01, 0x00, 0x01], [0x01, 0x02, 0x00, 0x01],
                 [0x01, 0x01, 0x36], [0x01, 0x02, 0x36]],
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private static IReadOnlyList<byte[]> CreateAutomaticStatePayloads(
        ThermalModeState state) => state switch
        {
            ThermalModeState.BalancedAutomatic =>
                [[0x01, 0x01, 0x00, 0x00], [0x01, 0x02, 0x00, 0x00]],
            ThermalModeState.SilentAutomatic =>
                [[0x01, 0x01, 0x05, 0x00], [0x01, 0x02, 0x05, 0x00]],
            ThermalModeState.PerformanceAutomatic =>
                [[0x01, 0x01, 0x02, 0x00], [0x01, 0x02, 0x02, 0x00]],
            ThermalModeState.BatterySaverAutomatic =>
                [[0x01, 0x01, 0x03, 0x00], [0x01, 0x02, 0x03, 0x00]],
            ThermalModeState.BatteryBalancedAutomatic =>
                [[0x01, 0x01, 0x06, 0x00], [0x01, 0x02, 0x06, 0x00]],
            _ => throw new InvalidOperationException(
                "Only a recognized automatic thermal state can be restored."),
        };

    private static TimeSpan FixedInterCommandDelay(
        PerformanceFanValidationTarget target,
        int completedCommandIndex) => target != PerformanceFanValidationTarget.Fixed5400
            ? TimeSpan.Zero
            : ThermalWriteTiming.MaximumInterCommandDelay(completedCommandIndex);

    private static bool IsBalancedAutomaticPayload(ReadOnlySpan<byte> payload) =>
        payload.Length == 4
        && payload[0] == 0x01
        && payload[1] is 0x01 or 0x02
        && payload[2] == 0x00
        && payload[3] == 0x00;

    private static ThermalModeState ExpectedState(
        PerformanceFanValidationTarget target) => target switch
        {
            PerformanceFanValidationTarget.Balanced => ThermalModeState.BalancedAutomatic,
            PerformanceFanValidationTarget.Silent => ThermalModeState.SilentAutomatic,
            PerformanceFanValidationTarget.Performance => ThermalModeState.PerformanceAutomatic,
            PerformanceFanValidationTarget.Fixed5400 => ThermalModeState.FixedMaximum,
            PerformanceFanValidationTarget.BatterySaver =>
                ThermalModeState.BatterySaverAutomatic,
            PerformanceFanValidationTarget.BatteryBalanced =>
                ThermalModeState.BatteryBalancedAutomatic,
            PerformanceFanValidationTarget.HyperBoost =>
                ThermalModeState.HyperBoostObserved,
            PerformanceFanValidationTarget.HyperBoostFromSilent =>
                ThermalModeState.HyperBoostObserved,
            PerformanceFanValidationTarget.HyperBoostFromPerformance =>
                ThermalModeState.HyperBoostObserved,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private static bool IsHyperBoostTarget(PerformanceFanValidationTarget target) =>
        target is PerformanceFanValidationTarget.HyperBoost
            or PerformanceFanValidationTarget.HyperBoostFromSilent
            or PerformanceFanValidationTarget.HyperBoostFromPerformance;

    private static ThermalModeState RequiredHyperBoostBaseline(
        PerformanceFanValidationTarget target) => target switch
        {
            PerformanceFanValidationTarget.HyperBoost =>
                ThermalModeState.BalancedAutomatic,
            PerformanceFanValidationTarget.HyperBoostFromSilent =>
                ThermalModeState.SilentAutomatic,
            PerformanceFanValidationTarget.HyperBoostFromPerformance =>
                ThermalModeState.PerformanceAutomatic,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private static PerformanceFanValidationFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked => PerformanceFanValidationFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked => PerformanceFanValidationFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed =>
                PerformanceFanValidationFailure.ConflictDetectionFailed,
            _ => PerformanceFanValidationFailure.OpenFailed,
        };

    private static PerformanceFanValidationFailure MapCancellation(
        HidSessionFailureKind? failure,
        CancellationToken cancellationToken,
        PerformanceFanValidationFailure fallback) =>
        cancellationToken.IsCancellationRequested
        && failure is HidSessionFailureKind.Canceled or HidSessionFailureKind.QueuedCanceled
            ? PerformanceFanValidationFailure.Canceled
            : fallback;

    private static PerformanceFanSetterValidationResult Empty(
        PerformanceFanValidationTarget target,
        PerformanceFanValidationFailure failure) => new(
        target, null, null, null, [], null, false, false, false, false, false,
        false, false, false, failure);

    private sealed record PairRead(
        byte[]? Thermal1,
        byte[]? Thermal2,
        PerformanceFanThermalPair? Pair,
        ThermalModeState? State,
        HidSessionFailureKind? Failure,
        bool PairMismatch)
    {
        internal bool Success => Failure is null && !PairMismatch && Pair is not null;
    }

    private sealed class RunState(PerformanceFanValidationTarget target)
    {
        internal PerformanceFanValidationTarget TargetForObserver => target;
        internal PerformanceFanThermalPair? ObserverFailureReconciliation { get; set; }
        internal PerformanceFanThermalPair? Baseline { get; set; }
        internal PerformanceFanThermalPair? Candidate { get; set; }
        internal FanRpmReadings? CandidateFanRpm { get; set; }
        internal List<FanRpmReadings> CandidateFanRpmSamples { get; } = [];
        internal PerformanceFanThermalPair? Restoration { get; set; }
        internal bool ApplyAttempted { get; set; }
        internal bool ApplyAcknowledged { get; set; }
        internal bool CandidateConfirmed { get; set; }
        internal bool RestoreAttempted { get; set; }
        internal bool RestoreAcknowledged { get; set; }
        internal bool Restored { get; set; }
        internal bool IndeterminatePartialWrite { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal bool FullPowerAdapterConfirmed { get; set; }
        internal OpenBlade.Contracts.AdapterPowerState InitialAdapterPowerState
        { get; set; }
        internal bool CoolingPadAbsentConfirmed { get; set; }
        internal bool CoolingPadPresentConfirmed { get; set; }
        internal bool CoolingPadOpaqueZeroConfirmed { get; set; }

        internal bool PostRestorationFullPowerAdapterConfirmed { get; set; }

        internal bool PostRestorationCoolingPadAbsentConfirmed { get; set; }

        internal PerformanceFanValidationSafetyFailure PostRestorationSafetyFailure
        { get; set; }

        internal PerformanceFanValidationSafetyFailure SafetyFailure { get; set; }
        internal bool BaselineBalancedConfirmed { get; set; }
        internal bool BaselineRequiredBaseModeConfirmed { get; set; }
        internal bool BalancedIntermediaryObserved { get; set; }
        internal HyperBoostSustainedValidationResult? SustainedValidation { get; set; }
        internal HyperBoostAdapterTransitionValidationResult? AdapterTransitionValidation
        { get; set; }
        internal HyperBoostPowerSourceBoundaryValidationResult? PowerSourceBoundaryValidation
        { get; set; }
        internal int RestoreWriteCount { get; set; }
        internal PerformanceFanValidationFailure Failure { get; set; }

        internal PerformanceFanSetterValidationResult ToResult() => new(
            target,
            Baseline,
            Candidate,
            CandidateFanRpm,
            CandidateFanRpmSamples.ToArray(),
            Restoration,
            ApplyAttempted,
            ApplyAcknowledged,
            CandidateConfirmed,
            RestoreAttempted,
            RestoreAcknowledged,
            Restored,
            IndeterminatePartialWrite,
            ManualRecoveryRequired,
            Failure)
        {
            FullPowerAdapterConfirmed = FullPowerAdapterConfirmed,
            ObserverFailureReconciliation = ObserverFailureReconciliation,
            InitialAdapterPowerState = InitialAdapterPowerState,
            CoolingPadAbsentConfirmed = CoolingPadAbsentConfirmed,
            CoolingPadPresentConfirmed = CoolingPadPresentConfirmed,
            CoolingPadOpaqueZeroConfirmed = CoolingPadOpaqueZeroConfirmed,
            PostRestorationFullPowerAdapterConfirmed =
                PostRestorationFullPowerAdapterConfirmed,
            PostRestorationCoolingPadAbsentConfirmed =
                PostRestorationCoolingPadAbsentConfirmed,
            PostRestorationSafetyFailure = PostRestorationSafetyFailure,
            SafetyFailure = SafetyFailure,
            BaselineBalancedConfirmed = BaselineBalancedConfirmed,
            BaselineRequiredBaseModeConfirmed = BaselineRequiredBaseModeConfirmed,
            BalancedIntermediaryObserved = BalancedIntermediaryObserved,
            SustainedValidation = SustainedValidation,
            AdapterTransitionValidation = AdapterTransitionValidation,
            PowerSourceBoundaryValidation = PowerSourceBoundaryValidation is null
                ? null
                : PowerSourceBoundaryValidation with
                {
                    OpenBladeCleanupWriteCount = RestoreWriteCount,
                },
        };
    }
}
