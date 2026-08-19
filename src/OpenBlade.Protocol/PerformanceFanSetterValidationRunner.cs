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
    BaselineNotRequiredBaseMode,
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
    internal bool FullPowerAdapterConfirmed { get; init; }

    internal bool CoolingPadPresentConfirmed { get; init; }

    internal bool CoolingPadOpaqueZeroConfirmed { get; init; }

    internal PerformanceFanValidationSafetyFailure SafetyFailure { get; init; }

    internal bool BaselineBalancedConfirmed { get; init; }

    internal bool BaselineRequiredBaseModeConfirmed { get; init; }

    internal bool BalancedIntermediaryObserved { get; init; }

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
    CoolingPadAbsent,
    CoolingPadAmbiguous,
    CoolingPadObservationFailed,
    CoolingPadQueryFailed,
    CoolingPadOpaqueZeroRequired,
    Canceled,
}

internal readonly record struct PerformanceFanValidationSafetyGateResult(
    bool FullPowerAdapterConfirmed,
    bool CoolingPadPresentConfirmed,
    bool CoolingPadOpaqueZeroConfirmed,
    PerformanceFanValidationSafetyFailure Failure)
{
    internal bool Success => Failure == PerformanceFanValidationSafetyFailure.None
        && FullPowerAdapterConfirmed
        && CoolingPadPresentConfirmed
        && CoolingPadOpaqueZeroConfirmed;
}

internal interface IPerformanceFanValidationSafetyGate
{
    Task<PerformanceFanValidationSafetyGateResult> ValidateAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken);
}

internal sealed class PadPresentHyperBoostSafetyGate(
    ICoolingPadDiscoverySource discovery,
    IProductionCoolingPadPortFactory portFactory,
    TimeSpan exchangeTimeout) : IPerformanceFanValidationSafetyGate
{
    private readonly ICoolingPadDiscoverySource discovery = discovery
        ?? throw new ArgumentNullException(nameof(discovery));
    private readonly IProductionCoolingPadPortFactory portFactory = portFactory
        ?? throw new ArgumentNullException(nameof(portFactory));
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

        if (power.Value != OpenBlade.Contracts.AdapterPowerState.FullPower280W)
        {
            return Failure(PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired);
        }

        OpenBlade.Contracts.CoolingPadConnectionState connection = discovery.Observe();
        if (connection != OpenBlade.Contracts.CoolingPadConnectionState.PresentExact)
        {
            return Failure(connection switch
            {
                OpenBlade.Contracts.CoolingPadConnectionState.Absent =>
                    PerformanceFanValidationSafetyFailure.CoolingPadAbsent,
                OpenBlade.Contracts.CoolingPadConnectionState.Ambiguous =>
                    PerformanceFanValidationSafetyFailure.CoolingPadAmbiguous,
                _ => PerformanceFanValidationSafetyFailure.CoolingPadObservationFailed,
            }, fullPowerConfirmed: true);
        }

        HidSessionResult<IProductionCoolingPadPort> opened =
            await portFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return Failure(
                cancellationToken.IsCancellationRequested
                    ? PerformanceFanValidationSafetyFailure.Canceled
                    : PerformanceFanValidationSafetyFailure.CoolingPadQueryFailed,
                fullPowerConfirmed: true,
                padPresentConfirmed: true);
        }

        IProductionCoolingPadPort port = opened.Value;
        HidSessionResult<CoolingPadFanObservation> observation;
        try
        {
            observation = await port.ObserveFanAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await port.DisposeAsync().AsTask()
                    .WaitAsync(exchangeTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                observation = new(default, HidSessionFailureKind.NativeIoFailure);
            }
        }

        if (observation.Failure is not null)
        {
            return Failure(
                cancellationToken.IsCancellationRequested
                    ? PerformanceFanValidationSafetyFailure.Canceled
                    : PerformanceFanValidationSafetyFailure.CoolingPadQueryFailed,
                fullPowerConfirmed: true,
                padPresentConfirmed: true);
        }

        if (observation.Value.OpaqueValue != 0)
        {
            return Failure(
                PerformanceFanValidationSafetyFailure.CoolingPadOpaqueZeroRequired,
                fullPowerConfirmed: true,
                padPresentConfirmed: true);
        }

        return new(
            FullPowerAdapterConfirmed: true,
            CoolingPadPresentConfirmed: true,
            CoolingPadOpaqueZeroConfirmed: true,
            PerformanceFanValidationSafetyFailure.None);
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        RazerCommandSessionTimeout.Validate(timeout);
        return timeout;
    }

    private static PerformanceFanValidationSafetyGateResult Failure(
        PerformanceFanValidationSafetyFailure failure,
        bool fullPowerConfirmed = false,
        bool padPresentConfirmed = false) => new(
        FullPowerAdapterConfirmed: fullPowerConfirmed,
        CoolingPadPresentConfirmed: padPresentConfirmed,
        CoolingPadOpaqueZeroConfirmed: false,
        failure);
}

public interface IPerformanceFanSetterValidationRunner
{
    Task<PerformanceFanSetterValidationResult> RunAsync(
        PerformanceFanValidationTarget target,
        CancellationToken cancellationToken);
}

internal sealed class PerformanceFanSetterValidationRunner
    : IPerformanceFanSetterValidationRunner
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
        IPerformanceFanValidationSafetyGate? safetyGate = null)
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
    }

    public async Task<PerformanceFanSetterValidationResult> RunAsync(
        PerformanceFanValidationTarget target,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(target) || Interlocked.Exchange(ref started, 1) != 0)
        {
            return Empty(target, PerformanceFanValidationFailure.OpenFailed);
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

            // Restoration starts only after both candidate channels have been read
            // successfully. A failed or mismatched paired read is indeterminate and
            // deliberately requires manual recovery instead of another write.
            state.RestoreAttempted = true;
            using CancellationTokenSource cleanup = new(cleanupDrainTimeout);
            foreach (byte[] payload in CreateAutomaticStatePayloads(baseline.State!.Value))
            {
                HidSessionResult<ReadOnlyMemory<byte>> write = await ExecuteAsync(
                    context,
                    actor.Session,
                    CreateEchoCommand(payload),
                    cleanup.Token).ConfigureAwait(false);
                if (write.Failure is not null)
                {
                    state.IndeterminatePartialWrite = true;
                    state.ManualRecoveryRequired = true;
                    state.Failure = PerformanceFanValidationFailure.RestoreWriteIndeterminate;
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
            else if (!state.CandidateConfirmed)
            {
                state.Failure = PerformanceFanValidationFailure.CandidateTargetMismatch;
            }
            else if (state.Failure == PerformanceFanValidationFailure.None)
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
        internal bool CoolingPadPresentConfirmed { get; set; }
        internal bool CoolingPadOpaqueZeroConfirmed { get; set; }
        internal PerformanceFanValidationSafetyFailure SafetyFailure { get; set; }
        internal bool BaselineBalancedConfirmed { get; set; }
        internal bool BaselineRequiredBaseModeConfirmed { get; set; }
        internal bool BalancedIntermediaryObserved { get; set; }
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
            CoolingPadPresentConfirmed = CoolingPadPresentConfirmed,
            CoolingPadOpaqueZeroConfirmed = CoolingPadOpaqueZeroConfirmed,
            SafetyFailure = SafetyFailure,
            BaselineBalancedConfirmed = BaselineBalancedConfirmed,
            BaselineRequiredBaseModeConfirmed = BaselineRequiredBaseModeConfirmed,
            BalancedIntermediaryObserved = BalancedIntermediaryObserved,
        };
    }
}
