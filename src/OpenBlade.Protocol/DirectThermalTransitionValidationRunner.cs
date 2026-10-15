using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal enum DirectThermalTransitionScenario
{
    ManualTargetRoundTrip,
    ManualToCustomAutomatic,
    ManualToHyperBoost,
}

internal enum DirectThermalTransitionFailure
{
    None,
    OpenFailed,
    BaselineReadFailed,
    BaselineMismatch,
    BaselineCustomReadFailed,
    SafetyAdmissionLost,
    PrepareWriteIndeterminate,
    PreparedReadFailed,
    PreparedMismatch,
    PreparedFanRpmReadFailed,
    PreparedFanRpmOutOfRange,
    TargetPreconditionReadFailed,
    TargetPreconditionMismatch,
    TargetWriteIndeterminate,
    CandidateReadFailed,
    CandidateMismatch,
    CandidateFanRpmReadFailed,
    CandidateFanRpmOutOfRange,
    CandidateCustomReadFailed,
    CandidateCustomMismatch,
    ReturnPreconditionReadFailed,
    ReturnPreconditionMismatch,
    ReturnWriteIndeterminate,
    ReturnReadFailed,
    ReturnMismatch,
    ReturnFanRpmReadFailed,
    ReturnFanRpmOutOfRange,
    RestorePreconditionReadFailed,
    RestorePreconditionMismatch,
    RestoreWriteIndeterminate,
    RestorationReadFailed,
    RestorationMismatch,
    RestorationCustomReadFailed,
    RestorationCustomMismatch,
    Canceled,
}

internal sealed record DirectThermalTransitionValidationResult(
    DirectThermalTransitionScenario Scenario,
    PerformanceFanThermalPair? Baseline,
    CustomPerformanceSettings? BaselineCustomPerformance,
    PerformanceFanThermalPair? PreparedManual,
    FanRpmReadings? PreparedFanRpm,
    PerformanceFanThermalPair? Candidate,
    FanRpmReadings? CandidateFanRpm,
    CustomPerformanceSettings? CandidateCustomPerformance,
    PerformanceFanThermalPair? ReturnManual,
    FanRpmReadings? ReturnFanRpm,
    PerformanceFanThermalPair? Restoration,
    CustomPerformanceSettings? RestorationCustomPerformance,
    int SafetyChecks,
    int WritesAttempted,
    bool Restored,
    bool IndeterminatePartialWrite,
    bool ManualRecoveryRequired,
    DirectThermalTransitionFailure Failure)
{
    internal bool Success => Failure == DirectThermalTransitionFailure.None
        && Restored
        && !IndeterminatePartialWrite
        && !ManualRecoveryRequired;
}

internal interface IDirectThermalTransitionValidationRunner
{
    Task<DirectThermalTransitionValidationResult> RunAsync(
        DirectThermalTransitionScenario scenario,
        CancellationToken cancellationToken);
}

/// <summary>
/// Developer-only validation for direct transitions from the exact captured manual-fan
/// state. Generic thermal readback cannot distinguish Fixed from Manual Curve and cannot
/// prove that a performance power limit remained effective while manual fans were active.
/// </summary>
internal sealed class DirectThermalTransitionValidationRunner
    : IDirectThermalTransitionValidationRunner
{
    private const int PreparedRpm = 5400;
    private const int CandidateRpm = 4000;
    private const int RequiredConsecutiveRpmSamples = 3;
    private const int DefaultMaximumRpmSamples = 120;
    private static readonly TimeSpan DefaultRpmSampleInterval = TimeSpan.FromSeconds(1);

    private readonly IValidationActorFactory actorFactory;
    private readonly IPerformanceFanValidationSafetyGate safetyGate;
    private readonly IPerformanceFanValidationSafetyGate? hyperBoostSafetyGate;
    private readonly DeviceCommandCatalog capturedCatalog;
    private readonly DeviceCommandCatalog? hyperBoostCatalog;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly TimeSpan rpmSampleInterval;
    private readonly int maximumRpmSamples;
    private readonly bool honorFanInterCommandTiming;
    private int started;

    internal DirectThermalTransitionValidationRunner(
        IValidationActorFactory actorFactory,
        IPerformanceFanValidationSafetyGate safetyGate,
        DeviceCommandCatalog capturedCatalog,
        DeviceCommandCatalog? hyperBoostCatalog,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        IPerformanceFanValidationSafetyGate? hyperBoostSafetyGate = null,
        PendingDisposalRegistry? pendingDisposals = null,
        TimeSpan? rpmSampleInterval = null,
        int maximumRpmSamples = DefaultMaximumRpmSamples,
        bool honorFanInterCommandTiming = true)
    {
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        this.capturedCatalog = capturedCatalog ?? throw new ArgumentNullException(nameof(capturedCatalog));
        this.hyperBoostCatalog = hyperBoostCatalog;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.hyperBoostSafetyGate = hyperBoostSafetyGate;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
        this.rpmSampleInterval = rpmSampleInterval ?? DefaultRpmSampleInterval;
        if (this.rpmSampleInterval < TimeSpan.Zero
            || this.rpmSampleInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(rpmSampleInterval));
        }

        if (maximumRpmSamples is < RequiredConsecutiveRpmSamples
            or > DefaultMaximumRpmSamples)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRpmSamples));
        }

        this.maximumRpmSamples = maximumRpmSamples;
        this.honorFanInterCommandTiming = honorFanInterCommandTiming;
    }

    public async Task<DirectThermalTransitionValidationResult> RunAsync(
        DirectThermalTransitionScenario scenario,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(scenario) || Interlocked.Exchange(ref started, 1) != 0)
        {
            return new RunState(scenario)
            {
                Failure = DirectThermalTransitionFailure.OpenFailed,
            }.ToResult();
        }

        if (scenario == DirectThermalTransitionScenario.ManualToHyperBoost
            && (hyperBoostCatalog is null || hyperBoostSafetyGate is null))
        {
            return new RunState(scenario)
            {
                Failure = DirectThermalTransitionFailure.OpenFailed,
            }.ToResult();
        }

        var state = new RunState(scenario);
        ValidationActor? actor = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new ValidationRunContext();
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                state.Failure = DirectThermalTransitionFailure.OpenFailed;
                return state.ToResult();
            }

            PairRead first = await ReadPairAsync(
                context, actor.Session, allowHyperBoost: false, cancellationToken)
                .ConfigureAwait(false);
            PairRead second = await ReadPairAsync(
                context, actor.Session, allowHyperBoost: false, cancellationToken)
                .ConfigureAwait(false);
            if (!first.Success || !second.Success)
            {
                state.Failure = DirectThermalTransitionFailure.BaselineReadFailed;
                return state.ToResult();
            }

            state.Baseline = first.Pair;
            if (first.State != ThermalModeState.BalancedAutomatic
                || second.State != ThermalModeState.BalancedAutomatic
                || first.Pair != second.Pair)
            {
                state.Failure = DirectThermalTransitionFailure.BaselineMismatch;
                return state.ToResult();
            }

            if (scenario == DirectThermalTransitionScenario.ManualToCustomAutomatic)
            {
                state.BaselineCustomPerformance = await ReadCustomPerformanceAsync(
                    context, actor.Session, cancellationToken).ConfigureAwait(false);
                if (state.BaselineCustomPerformance is null)
                {
                    state.Failure = DirectThermalTransitionFailure.BaselineCustomReadFailed;
                    return state.ToResult();
                }
            }

            IReadOnlyList<RazerCommandDefinition> prepare =
            [
                Required(capturedCatalog, ProductionBatteryPort.FanMaximumStateFirstAction),
                Required(capturedCatalog, ProductionBatteryPort.FanMaximumStateSecondAction),
                FanSpeedCommand(capturedCatalog, ThermalChannel.First, PreparedRpm),
                FanSpeedCommand(capturedCatalog, ThermalChannel.Second, PreparedRpm),
            ];
            if (!await WriteAllAsync(context, actor.Session, prepare, SafetyFor(scenario),
                state, DirectThermalTransitionFailure.PrepareWriteIndeterminate,
                fanTimingOffset: 0,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return state.ToResult();
            }

            PairRead prepared = await ReadPairAsync(
                context, actor.Session, allowHyperBoost: false, cancellationToken)
                .ConfigureAwait(false);
            if (!prepared.Success)
            {
                return FailAfterWrite(state, DirectThermalTransitionFailure.PreparedReadFailed);
            }
            state.PreparedManual = prepared.Pair;
            if (prepared.State != ThermalModeState.FixedMaximum)
            {
                return FailAfterWrite(state, DirectThermalTransitionFailure.PreparedMismatch);
            }
            FanRpmReadings? preparedRpm = await ReadSettledRpmAsync(
                context, actor.Session, PreparedRpm, cancellationToken).ConfigureAwait(false);
            if (preparedRpm is null)
            {
                return FailAfterWrite(state,
                    DirectThermalTransitionFailure.PreparedFanRpmOutOfRange);
            }
            state.PreparedFanRpm = preparedRpm;

            PairRead targetPrecondition = await ReadPairAsync(
                context, actor.Session, allowHyperBoost: false, cancellationToken)
                .ConfigureAwait(false);
            if (!targetPrecondition.Success)
            {
                return FailAfterWrite(state,
                    DirectThermalTransitionFailure.TargetPreconditionReadFailed);
            }
            if (targetPrecondition.State != ThermalModeState.FixedMaximum)
            {
                return FailAfterWrite(state,
                    DirectThermalTransitionFailure.TargetPreconditionMismatch);
            }

            IReadOnlyList<RazerCommandDefinition> target = scenario switch
            {
                DirectThermalTransitionScenario.ManualTargetRoundTrip =>
                [
                    FanSpeedCommand(capturedCatalog, ThermalChannel.First, CandidateRpm),
                    FanSpeedCommand(capturedCatalog, ThermalChannel.Second, CandidateRpm),
                ],
                DirectThermalTransitionScenario.ManualToCustomAutomatic =>
                    CustomCommands(state.BaselineCustomPerformance!),
                DirectThermalTransitionScenario.ManualToHyperBoost =>
                [
                    Required(hyperBoostCatalog!, ProductionBatteryPort.HyperBoostEnableFirstAction),
                    Required(hyperBoostCatalog!, ProductionBatteryPort.HyperBoostEnableSecondAction),
                ],
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
            if (!await WriteAllAsync(context, actor.Session, target, SafetyFor(scenario),
                state, DirectThermalTransitionFailure.TargetWriteIndeterminate,
                fanTimingOffset:
                    scenario == DirectThermalTransitionScenario.ManualTargetRoundTrip ? 2 : -1,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return state.ToResult();
            }

            PairRead candidate = await ReadPairAsync(
                context, actor.Session,
                allowHyperBoost: scenario == DirectThermalTransitionScenario.ManualToHyperBoost,
                cancellationToken).ConfigureAwait(false);
            if (!candidate.Success)
            {
                return FailAfterWrite(state, DirectThermalTransitionFailure.CandidateReadFailed);
            }
            state.Candidate = candidate.Pair;
            ThermalModeState expected = scenario switch
            {
                DirectThermalTransitionScenario.ManualTargetRoundTrip =>
                    ThermalModeState.FixedMaximum,
                DirectThermalTransitionScenario.ManualToCustomAutomatic =>
                    ThermalModeState.CustomAutomatic,
                DirectThermalTransitionScenario.ManualToHyperBoost =>
                    ThermalModeState.HyperBoostObserved,
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
            if (candidate.State != expected)
            {
                return FailAfterWrite(state, DirectThermalTransitionFailure.CandidateMismatch);
            }

            if (scenario == DirectThermalTransitionScenario.ManualTargetRoundTrip)
            {
                state.CandidateFanRpm = await ReadSettledRpmAsync(
                    context, actor.Session, CandidateRpm, cancellationToken).ConfigureAwait(false);
                if (state.CandidateFanRpm is null)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.CandidateFanRpmOutOfRange);
                }

                PairRead returnPrecondition = await ReadPairAsync(
                    context, actor.Session, allowHyperBoost: false, cancellationToken)
                    .ConfigureAwait(false);
                if (!returnPrecondition.Success)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.ReturnPreconditionReadFailed);
                }
                if (returnPrecondition.State != ThermalModeState.FixedMaximum)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.ReturnPreconditionMismatch);
                }

                IReadOnlyList<RazerCommandDefinition> returnToFixed =
                [
                    FanSpeedCommand(capturedCatalog, ThermalChannel.First, PreparedRpm),
                    FanSpeedCommand(capturedCatalog, ThermalChannel.Second, PreparedRpm),
                ];
                if (!await WriteAllAsync(context, actor.Session, returnToFixed,
                    SafetyFor(scenario), state,
                    DirectThermalTransitionFailure.ReturnWriteIndeterminate,
                    fanTimingOffset: 2,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    return state.ToResult();
                }
                PairRead returned = await ReadPairAsync(
                    context, actor.Session, allowHyperBoost: false, cancellationToken)
                    .ConfigureAwait(false);
                if (!returned.Success)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.ReturnReadFailed);
                }
                state.ReturnManual = returned.Pair;
                if (returned.State != ThermalModeState.FixedMaximum)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.ReturnMismatch);
                }
                state.ReturnFanRpm = await ReadSettledRpmAsync(
                    context, actor.Session, PreparedRpm, cancellationToken).ConfigureAwait(false);
                if (state.ReturnFanRpm is null)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.ReturnFanRpmOutOfRange);
                }
            }
            else if (scenario == DirectThermalTransitionScenario.ManualToCustomAutomatic)
            {
                state.CandidateCustomPerformance = await ReadCustomPerformanceAsync(
                    context, actor.Session, cancellationToken).ConfigureAwait(false);
                if (state.CandidateCustomPerformance is null)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.CandidateCustomReadFailed);
                }
                if (state.CandidateCustomPerformance != state.BaselineCustomPerformance)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.CandidateCustomMismatch);
                }
            }

            PairRead restorePrecondition = await ReadPairAsync(
                context,
                actor.Session,
                allowHyperBoost:
                    scenario == DirectThermalTransitionScenario.ManualToHyperBoost,
                cancellationToken).ConfigureAwait(false);
            if (!restorePrecondition.Success)
            {
                return FailAfterWrite(state,
                    DirectThermalTransitionFailure.RestorePreconditionReadFailed);
            }
            if (restorePrecondition.State != expected)
            {
                return FailAfterWrite(state,
                    DirectThermalTransitionFailure.RestorePreconditionMismatch);
            }

            IReadOnlyList<RazerCommandDefinition> restore =
            [
                Required(capturedCatalog, ProductionBatteryPort.PerformanceBalancedFirstAction),
                Required(capturedCatalog, ProductionBatteryPort.PerformanceBalancedSecondAction),
            ];
            if (!await WriteAllAsync(context, actor.Session, restore, SafetyFor(scenario),
                state, DirectThermalTransitionFailure.RestoreWriteIndeterminate,
                fanTimingOffset: -1,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return state.ToResult();
            }

            PairRead restoration = await ReadPairAsync(
                context, actor.Session, allowHyperBoost: false, cancellationToken)
                .ConfigureAwait(false);
            if (!restoration.Success)
            {
                return FailAfterWrite(state,
                    DirectThermalTransitionFailure.RestorationReadFailed);
            }
            state.Restoration = restoration.Pair;
            if (restoration.State != ThermalModeState.BalancedAutomatic
                || restoration.Pair != state.Baseline)
            {
                return FailAfterWrite(state,
                    DirectThermalTransitionFailure.RestorationMismatch);
            }

            if (scenario == DirectThermalTransitionScenario.ManualToCustomAutomatic)
            {
                state.RestorationCustomPerformance = await ReadCustomPerformanceAsync(
                    context, actor.Session, cancellationToken).ConfigureAwait(false);
                if (state.RestorationCustomPerformance is null)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.RestorationCustomReadFailed);
                }
                if (state.RestorationCustomPerformance != state.BaselineCustomPerformance)
                {
                    return FailAfterWrite(state,
                        DirectThermalTransitionFailure.RestorationCustomMismatch);
                }
            }

            state.Restored = true;
            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Failure = DirectThermalTransitionFailure.Canceled;
            state.ManualRecoveryRequired |= state.WritesAttempted > 0 && !state.Restored;
            return state.ToResult();
        }
        catch
        {
            state.Failure = state.WritesAttempted > 0
                ? DirectThermalTransitionFailure.TargetWriteIndeterminate
                : DirectThermalTransitionFailure.OpenFailed;
            state.IndeterminatePartialWrite |= state.WritesAttempted > 0;
            state.ManualRecoveryRequired |= state.WritesAttempted > 0;
            return state.ToResult();
        }
        finally
        {
            if (actor is not null)
            {
                await BoundedValidationActorDisposal.DisposeAsync(
                    actor, timeProvider, cleanupDrainTimeout, pendingDisposals)
                    .ConfigureAwait(false);
            }
        }
    }

    private IPerformanceFanValidationSafetyGate SafetyFor(
        DirectThermalTransitionScenario scenario) =>
        scenario == DirectThermalTransitionScenario.ManualToHyperBoost
            ? hyperBoostSafetyGate!
            : safetyGate;

    private IReadOnlyList<RazerCommandDefinition> CustomCommands(
        CustomPerformanceSettings custom) =>
    [
        Required(capturedCatalog, ProductionBatteryPort.PerformanceCustomFirstAction),
        Required(capturedCatalog, ProductionBatteryPort.PerformanceCustomSecondAction),
        Required(capturedCatalog, CustomAction(CustomPerformanceDomain.Cpu, custom.Cpu)),
        Required(capturedCatalog, CustomAction(CustomPerformanceDomain.Gpu, custom.Gpu)),
    ];

    private static string CustomAction(
        CustomPerformanceDomain domain,
        CustomPerformanceLevel level) => (domain, level) switch
        {
            (CustomPerformanceDomain.Cpu, CustomPerformanceLevel.Low) =>
                ProductionBatteryPort.CustomCpuLowAction,
            (CustomPerformanceDomain.Cpu, CustomPerformanceLevel.Medium) =>
                ProductionBatteryPort.CustomCpuMediumAction,
            (CustomPerformanceDomain.Cpu, CustomPerformanceLevel.High) =>
                ProductionBatteryPort.CustomCpuHighAction,
            (CustomPerformanceDomain.Gpu, CustomPerformanceLevel.Low) =>
                ProductionBatteryPort.CustomGpuLowAction,
            (CustomPerformanceDomain.Gpu, CustomPerformanceLevel.Medium) =>
                ProductionBatteryPort.CustomGpuMediumAction,
            (CustomPerformanceDomain.Gpu, CustomPerformanceLevel.High) =>
                ProductionBatteryPort.CustomGpuHighAction,
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };

    private static RazerCommandDefinition Required(
        DeviceCommandCatalog catalog,
        string action) => catalog.TryGet(action, out RazerCommandDefinition? command)
            && command is not null
                ? command
                : throw new UnvalidatedCommandException(
                    $"Required captured action '{action}' is absent.");

    private static RazerCommandDefinition FanSpeedCommand(
        DeviceCommandCatalog catalog,
        ThermalChannel channel,
        int rpm)
    {
        string action = channel == ThermalChannel.First
            ? ProductionBatteryPort.FanMaximumSpeedFirstAction
            : ProductionBatteryPort.FanMaximumSpeedSecondAction;
        RazerCommandDefinition template = Required(catalog, action);
        if (template.CommandClass != ThermalModeProtocol.CommandClass
            || template.CommandId != 0x01
            || template.Payload.Length != 3
            || template.Payload[0] != 0x01
            || template.Payload[1] != (byte)channel)
        {
            throw new UnvalidatedCommandException(
                $"Captured fan target template '{action}' is malformed.");
        }

        byte[] payload = [0x01, (byte)channel, checked((byte)(rpm / 100))];
        return new RazerCommandDefinition(
            template.CommandClass, template.CommandId, payload, payload);
    }

    private async Task<bool> WriteAllAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        IReadOnlyList<RazerCommandDefinition> commands,
        IPerformanceFanValidationSafetyGate gate,
        RunState state,
        DirectThermalTransitionFailure failure,
        int fanTimingOffset,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < commands.Count; index++)
        {
            RazerCommandDefinition command = commands[index];
            PerformanceFanValidationSafetyGateResult admission =
                await gate.ValidateAsync(context, session, cancellationToken)
                    .ConfigureAwait(false);
            state.SafetyChecks++;
            if (!admission.Success)
            {
                state.Failure = DirectThermalTransitionFailure.SafetyAdmissionLost;
                state.ManualRecoveryRequired = state.WritesAttempted > 0;
                return false;
            }

            state.WritesAttempted++;
            HidSessionResult<ReadOnlyMemory<byte>> write = await ExecuteAsync(
                context,
                session,
                new RazerCommandSpec(
                    command.CommandClass,
                    command.CommandId,
                    command.Payload.ToArray(),
                    command.ExpectedResponsePayload.Length,
                    response => response.Span.SequenceEqual(
                        command.ExpectedResponsePayload.AsSpan())),
                cancellationToken).ConfigureAwait(false);
            if (write.Failure is not null
                || !write.Value.Span.SequenceEqual(
                    command.ExpectedResponsePayload.AsSpan()))
            {
                state.Failure = failure;
                state.IndeterminatePartialWrite = true;
                state.ManualRecoveryRequired = true;
                return false;
            }

            if (fanTimingOffset >= 0 && honorFanInterCommandTiming
                && index < commands.Count - 1)
            {
                await Task.Delay(
                    ThermalWriteTiming.MaximumInterCommandDelay(fanTimingOffset + index),
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    private async Task<PairRead> ReadPairAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        bool allowHyperBoost,
        CancellationToken cancellationToken)
    {
        var payloads = new byte[2][];
        for (byte value = 1; value <= 2; value++)
        {
            ThermalChannel channel = (ThermalChannel)value;
            byte[] request = ThermalModeProtocol.CreateQueryPayload(channel);
            HidSessionResult<ReadOnlyMemory<byte>> read = await ExecuteAsync(
                context,
                session,
                new RazerCommandSpec(
                    ThermalModeProtocol.CommandClass,
                    ThermalModeProtocol.QueryCommandId,
                    request,
                    4,
                    payload => ThermalModeProtocol.TryDecodeQuery(
                        payload.Span, channel, out _)),
                cancellationToken).ConfigureAwait(false);
            if (read.Failure is not null)
            {
                return new(null, null, false);
            }
            payloads[value - 1] = read.Value.ToArray();
        }

        if (!ThermalModeProtocol.TryDecodeMatchingPair(
                payloads[0], payloads[1], out ThermalModeState state)
            || state == ThermalModeState.HyperBoostObserved && !allowHyperBoost)
        {
            return new(null, null, false);
        }

        return new(
            new PerformanceFanThermalPair(
                Convert.ToHexString(payloads[0]),
                Convert.ToHexString(payloads[1])),
            state,
            true);
    }

    private async Task<CustomPerformanceSettings?> ReadCustomPerformanceAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken)
    {
        async Task<CustomPerformanceLevel?> ReadAsync(CustomPerformanceDomain domain)
        {
            HidSessionResult<ReadOnlyMemory<byte>> read = await ExecuteAsync(
                context,
                session,
                new RazerCommandSpec(
                    CustomPerformanceProtocol.CommandClass,
                    CustomPerformanceProtocol.QueryCommandId,
                    CustomPerformanceProtocol.CreateQueryPayload(domain),
                    3,
                    payload => CustomPerformanceProtocol.TryDecodeQuery(
                        payload.Span, domain, out _)),
                cancellationToken).ConfigureAwait(false);
            return read.Failure is null
                && CustomPerformanceProtocol.TryDecodeQuery(
                    read.Value.Span, domain, out CustomPerformanceLevel level)
                    ? level
                    : null;
        }

        CustomPerformanceLevel? cpu = await ReadAsync(CustomPerformanceDomain.Cpu)
            .ConfigureAwait(false);
        CustomPerformanceLevel? gpu = await ReadAsync(CustomPerformanceDomain.Gpu)
            .ConfigureAwait(false);
        if (cpu is not { } cpuValue
            || gpu is not { } gpuValue
            || !Enum.IsDefined(cpuValue)
            || !Enum.IsDefined(gpuValue)
            || (cpuValue, gpuValue) is
                (CustomPerformanceLevel.High, CustomPerformanceLevel.High))
        {
            return null;
        }

        return new CustomPerformanceSettings(cpuValue, gpuValue);
    }

    private async Task<FanRpmReadings?> ReadSettledRpmAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        int requestedRpm,
        CancellationToken cancellationToken)
    {
        int consecutive = 0;
        FanRpmReadings? latest = null;
        int minimum = requestedRpm == PreparedRpm ? 5000 : 3500;
        int maximum = requestedRpm == PreparedRpm ? 6000 : 4500;
        for (int sample = 0; sample < maximumRpmSamples; sample++)
        {
            if (rpmSampleInterval > TimeSpan.Zero)
            {
                await Task.Delay(rpmSampleInterval, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }

            int[] values = new int[2];
            for (byte value = 1; value <= 2; value++)
            {
                FanRpmSelector selector = (FanRpmSelector)value;
                byte[] request = [0x01, value, 0x00];
                HidSessionResult<ReadOnlyMemory<byte>> read = await ExecuteAsync(
                    context,
                    session,
                    new RazerCommandSpec(
                        0x0D,
                        0x88,
                        request,
                        3,
                        payload => FanRpmProtocol.TryDecodeQuery(
                            payload.Span, selector, out _)),
                    cancellationToken).ConfigureAwait(false);
                if (read.Failure is not null
                    || !FanRpmProtocol.TryDecodeQuery(
                        read.Value.Span, selector, out values[value - 1]))
                {
                    return null;
                }
            }

            latest = new FanRpmReadings(values[0], values[1]);
            consecutive = values.All(rpm => rpm >= minimum && rpm <= maximum)
                ? consecutive + 1
                : 0;
            if (consecutive >= RequiredConsecutiveRpmSamples)
            {
                return latest;
            }
        }

        return null;
    }

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

    private static DirectThermalTransitionValidationResult FailAfterWrite(
        RunState state,
        DirectThermalTransitionFailure failure)
    {
        state.Failure = failure;
        state.ManualRecoveryRequired = true;
        return state.ToResult();
    }

    private sealed record PairRead(
        PerformanceFanThermalPair? Pair,
        ThermalModeState? State,
        bool Success);

    private sealed class RunState(DirectThermalTransitionScenario scenario)
    {
        internal PerformanceFanThermalPair? Baseline { get; set; }
        internal CustomPerformanceSettings? BaselineCustomPerformance { get; set; }
        internal PerformanceFanThermalPair? PreparedManual { get; set; }
        internal FanRpmReadings? PreparedFanRpm { get; set; }
        internal PerformanceFanThermalPair? Candidate { get; set; }
        internal FanRpmReadings? CandidateFanRpm { get; set; }
        internal CustomPerformanceSettings? CandidateCustomPerformance { get; set; }
        internal PerformanceFanThermalPair? ReturnManual { get; set; }
        internal FanRpmReadings? ReturnFanRpm { get; set; }
        internal PerformanceFanThermalPair? Restoration { get; set; }
        internal CustomPerformanceSettings? RestorationCustomPerformance { get; set; }
        internal int SafetyChecks { get; set; }
        internal int WritesAttempted { get; set; }
        internal bool Restored { get; set; }
        internal bool IndeterminatePartialWrite { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal DirectThermalTransitionFailure Failure { get; set; }

        internal DirectThermalTransitionValidationResult ToResult() => new(
            scenario,
            Baseline,
            BaselineCustomPerformance,
            PreparedManual,
            PreparedFanRpm,
            Candidate,
            CandidateFanRpm,
            CandidateCustomPerformance,
            ReturnManual,
            ReturnFanRpm,
            Restoration,
            RestorationCustomPerformance,
            SafetyChecks,
            WritesAttempted,
            Restored,
            IndeterminatePartialWrite,
            ManualRecoveryRequired,
            Failure);
    }
}
