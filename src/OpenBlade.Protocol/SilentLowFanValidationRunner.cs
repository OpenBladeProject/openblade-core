namespace OpenBlade.Protocol;

internal enum SilentLowFanScenario { FanStop1500, LowestSpinning, LiteralZero }

internal sealed record SilentLowFanStep(int RequestedRpm,
    PerformanceFanThermalPair? Pair, FanRpmReadings? Targets,
    string Observation, IReadOnlyList<LowFanValidationSample> Samples);

internal sealed record SilentLowFanValidationResult(bool Success, bool Restored,
    bool ManualRecoveryRequired, string? Failure, int WritesAttempted,
    bool IndeterminatePartialWrite, SilentLowFanScenario Scenario,
    PerformanceFanThermalPair? Baseline, FanRpmReadings? BaselineTargets,
    FanRpmReadings? BaselineFanRpm, SilentLowFanStep? Preparation,
    IReadOnlyList<SilentLowFanStep> Steps, int? LowestObservedSpinningTargetRpm,
    PerformanceFanThermalPair? Restoration, FanRpmReadings? RestorationTargets);

internal sealed record SilentLowFanProgress(string Stage, DateTimeOffset Timestamp,
    bool Restoring, string? PayloadHex, SilentLowFanValidationResult State);

/// <summary>
/// Developer-only Product736 Silent experiment. Closed targets do not change product
/// capabilities. The 2000 preparation establishes physical causality, not a firmware
/// prerequisite. Full adapter power is required because the low-power vendor path
/// normalizes Silent to Balanced. Literal zero is a separate developer experiment,
/// requiring its own explicit approval; its firmware meaning is unknown. No 100 RPM
/// command is exposed here.
/// </summary>
internal sealed class SilentLowFanValidationRunner(
    IValidationActorFactory actors, DeviceCommandCatalog catalog,
    Func<bool, CancellationToken, Task<string?>> admission,
    Func<bool, bool> submissionAllowed, Action<SilentLowFanProgress> journal,
    TimeProvider timeProvider, TimeSpan exchangeTimeout, TimeSpan cleanupDrainTimeout,
    bool honorFanInterCommandTiming = true)
{
    private int started;
    private const int MaximumSamples = 30;
    private static readonly TimeSpan MaximumAdmissionAge = TimeSpan.FromSeconds(2);

    internal async Task<SilentLowFanValidationResult> RunAsync(
        SilentLowFanScenario scenario, CancellationToken token)
    {
        var state = new State(scenario);
        try
        {
            if (!Enum.IsDefined(scenario) || Interlocked.Exchange(ref started, 1) != 0)
            {
                throw new Stop("InvalidScenarioOrAlreadyRun");
            }

            ValidateTemplates();
            var first = await ReadStateAsync(state, false, token);
            var second = await ReadStateAsync(state, false, token);
            state.Baseline = first.Pair;
            state.BaselineTargets = first.Targets;
            if (first.Mode != ThermalModeState.SilentAutomatic
                || second.Mode != first.Mode || first.Pair != second.Pair || first.Targets != second.Targets)
            {
                throw new Stop("BaselineMustBeStableSilentAutomatic");
            }

            if (!Restorable(first.Targets.Selector1Rpm) || !Restorable(first.Targets.Selector2Rpm))
            {
                throw new Stop("BaselineTargetsNotRestorable");
            }

            state.BaselineFanRpm = await ReadTachAsync(state, token);
            Journal("Baseline", false, null, state);

            // This new manual state must be independently confirmed before any RPM SET.
            await WritePairAsync(Mode(1, true), Mode(2, true), state, false, token);
            var manual = await ReadStateAsync(state, false, token);
            if (manual.Mode != ThermalModeState.SilentManual)
            {
                throw new Stop("SilentManualReadAmbiguous", true);
            }

            Journal("SilentManualConfirmed", false, null, state);
            state.Preparation = await ObserveTargetAsync(2000, true, state, token);
            if (state.Preparation.Observation != "StableSpinning")
            {
                state.Failure = "Preparation:" + state.Preparation.Observation;
            }
            else
            {
                state.Lowest = 2000;
                int[] candidates = CandidateTargets(scenario);
                foreach (int rpm in candidates)
                {
                    SilentLowFanStep step = await ObserveTargetAsync(rpm, false, state, token);
                    if (step.Observation == "StableSpinning")
                    {
                        state.Lowest = rpm;
                    }

                    if (scenario is SilentLowFanScenario.FanStop1500 or SilentLowFanScenario.LiteralZero)
                    {
                        if (step.Observation != "BothStopped")
                        {
                            state.Failure = step.Observation;
                        }

                        break;
                    }
                    if (step.Observation is "BothStopped" or "EitherStopped")
                    {
                        break;
                    }

                    if (step.Observation != "StableSpinning")
                    {
                        state.Failure = step.Observation;
                        break;
                    }
                }
            }
            await RestoreAsync(state, token);
        }
        catch (Stop stop)
        {
            state.Failure = stop.Message;
            state.Ambiguous |= stop.Ambiguous;
            // A denied trial admission is known not to submit. Restore only after
            // independent GETs and fresh restoration admission, with no heat ceiling.
            if (stop.KnownSafetyLoss && state.Writes > 0 && !state.Ambiguous)
            {
                try { await RestoreAsync(state, token); }
                catch (Stop restore) { state.Failure += ";" + restore.Message; state.Ambiguous |= restore.Ambiguous; }
                catch (Exception ex) { state.Failure += ";" + ex.GetType().Name; state.Ambiguous = true; }
            }
        }
        catch (OperationCanceledException)
        {
            state.Failure = "Canceled";
            state.Ambiguous |= state.Writes > 0;
        }
        catch (Exception ex)
        {
            state.Failure = "UnexpectedFailure:" + ex.GetType().Name;
            state.Ambiguous |= state.Writes > 0;
        }
        return state.Result();
    }

    private async Task<SilentLowFanStep> ObserveTargetAsync(int rpm, bool preparation,
        State state, CancellationToken token)
    {
        await WritePairAsync(Speed(1, rpm), Speed(2, rpm), state, false, token);
        var read = await ReadStateAsync(state, false, token);
        if (read.Mode != ThermalModeState.SilentManual)
        {
            throw new Stop("TargetModeReadAmbiguous", true);
        }

        var samples = new List<LowFanValidationSample>();
        SilentLowFanStep Publish(string observation)
        {
            var result = new SilentLowFanStep(rpm, read.Pair, read.Targets, observation, samples.ToArray());
            if (preparation)
            {
                state.Preparation = result;
            }
            else if (state.Steps.Count > 0 && state.Steps[^1].RequestedRpm == rpm)
            {
                state.Steps[^1] = result;
            }
            else
            {
                state.Steps.Add(result);
            }

            Journal(preparation ? "Preparation" : "TargetObservation", false, null, state);
            return result;
        }
        if (read.Targets != new FanRpmReadings(rpm, rpm))
        {
            return Publish("StoredTargetClamped");
        }

        Publish("Observing");
        async Task<SilentLowFanStep> FinishAsync(string observation)
        {
            // Physical samples cannot establish the requested mode/target. Confirm
            // them again at the observation boundary before publishing an outcome.
            read = await ReadStateAsync(state, false, token);
            if (read.Mode != ThermalModeState.SilentManual)
            {
                throw new Stop("ObservationModeReadAmbiguous", true);
            }
            return Publish(read.Targets == new FanRpmReadings(rpm, rpm) ? observation : "StoredTargetClamped");
        }
        long targetConfirmedAt = timeProvider.GetTimestamp();
        int zeroFirst = 0, zeroSecond = 0, bothZero = 0, spinning = 0;
        for (int i = 1; i <= MaximumSamples; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, token);
            FanRpmReadings tach = await ReadTachAsync(state, token);
            samples.Add(new(i, timeProvider.GetUtcNow(), tach));
            zeroFirst = tach.Selector1Rpm == 0 ? zeroFirst + 1 : 0;
            zeroSecond = tach.Selector2Rpm == 0 ? zeroSecond + 1 : 0;
            bothZero = tach.Selector1Rpm == 0 && tach.Selector2Rpm == 0 ? bothZero + 1 : 0;
            spinning = InRange(tach.Selector1Rpm, rpm) && InRange(tach.Selector2Rpm, rpm) ? spinning + 1 : 0;
            Publish("Observing");
            if (bothZero >= 3)
            {
                return await FinishAsync("BothStopped");
            }

            if (!preparation && state.Scenario == SilentLowFanScenario.LowestSpinning
                && (zeroFirst >= 3 || zeroSecond >= 3))
            {
                return await FinishAsync("EitherStopped");
            }

            bool heldLongEnough = preparation || timeProvider.GetElapsedTime(targetConfirmedAt) >= TimeSpan.FromSeconds(20);
            if (spinning >= 3 && heldLongEnough && StableLastThree(samples)
                && (preparation || state.Scenario == SilentLowFanScenario.LowestSpinning))
            {
                return await FinishAsync("StableSpinning");
            }
        }
        return await FinishAsync(!preparation && (state.Scenario is SilentLowFanScenario.FanStop1500 or SilentLowFanScenario.LiteralZero)
            && spinning >= 3 && StableLastThree(samples) ? "FansDidNotStop" : "RpmDidNotSettle");
    }

    internal static int[] CandidateTargets(SilentLowFanScenario scenario) => scenario switch
    {
        SilentLowFanScenario.FanStop1500 => [1500],
        SilentLowFanScenario.LowestSpinning => [1900, 1800, 1700, 1600, 1500],
        SilentLowFanScenario.LiteralZero => [0],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private async Task RestoreAsync(State state, CancellationToken token)
    {
        if (state.Ambiguous || state.BaselineTargets is not { } targets)
        {
            throw new Stop("RestorationBlocked", true);
        }

        var current = await ReadStateAsync(state, true, token);
        if (current.Mode != ThermalModeState.SilentManual)
        {
            throw new Stop("RestorePreconditionReadAmbiguous", true);
        }

        await WritePairAsync(Speed(1, targets.Selector1Rpm), Speed(2, targets.Selector2Rpm), state, true, token);
        await WritePairAsync(Mode(1, false), Mode(2, false), state, true, token);
        var restored = await ReadStateAsync(state, true, token);
        state.Restoration = restored.Pair;
        state.RestorationTargets = restored.Targets;
        if (restored.Mode != ThermalModeState.SilentAutomatic || restored.Pair != state.Baseline || restored.Targets != targets)
        {
            throw new Stop("RestorationNotConfirmed", true);
        }

        state.Restored = true;
        Journal("Restored", true, null, state);
    }

    private async Task WritePairAsync(RazerCommandDefinition first, RazerCommandDefinition second,
        State state, bool restoring, CancellationToken token)
    {
        await WriteAsync(first, state, restoring, token);
        if (honorFanInterCommandTiming)
        {
            await Task.Delay(ThermalWriteTiming.MaximumInterCommandDelay(first.CommandId == 2 ? 0 : 2), timeProvider, token);
        }

        await WriteAsync(second, state, restoring, token);
    }

    private Task WriteAsync(RazerCommandDefinition command, State state, bool restoring, CancellationToken token) =>
        WithActorAsync(state, restoring, token, async (session, context, adapterTimestamp) =>
        {
            Journal("WriteIntent", restoring, command, state);
            token.ThrowIfCancellationRequested();
            var spec = new RazerCommandSpec(command.CommandClass, command.CommandId, command.Payload.ToArray(),
                command.ExpectedResponsePayload.Length,
                p => p.Span.SequenceEqual(command.ExpectedResponsePayload.AsSpan()))
            {
                SubmissionAllowed = () => submissionAllowed(restoring)
                    && timeProvider.GetElapsedTime(adapterTimestamp) <= MaximumAdmissionAge,
            };
            state.Writes++;
            var write = await ExecuteAsync(session, context, spec, token);
            if (write.Failure is not null || !write.Value.Span.SequenceEqual(command.ExpectedResponsePayload.AsSpan()))
            {
                throw new Stop(restoring ? "RestoreWriteIndeterminate" : "TrialWriteIndeterminate", true);
            }

            Journal("WriteAcknowledged", restoring, command, state);
            return true;
        });

    private Task<StateRead> ReadStateAsync(State state, bool restoring, CancellationToken token) =>
        WithActorAsync(state, restoring, token, async (session, context, ignoredTimestamp) =>
        {
            byte[][] payloads = new byte[2][];
            for (byte zone = 1; zone <= 2; zone++)
            {
                ThermalChannel channel = (ThermalChannel)zone;
                var result = await ExecuteAsync(session, context, new(0x0D, 0x82,
                    ThermalModeProtocol.CreateQueryPayload(channel), 4,
                    p => ThermalModeProtocol.TryDecodeQuery(p.Span, channel, out _)), token);
                if (result.Failure is not null)
                {
                    throw new Stop("ModeReadAmbiguous", state.Writes > 0);
                }

                payloads[zone - 1] = result.Value.ToArray();
            }
            if (!ThermalModeProtocol.TryDecodeMatchingPair(payloads[0], payloads[1], out ThermalModeState mode))
            {
                throw new Stop("ModeReadAmbiguous", state.Writes > 0);
            }

            var targets = await ReadSpeedsAsync(session, context, 0x81, state, token);
            return new StateRead(new(Convert.ToHexString(payloads[0]), Convert.ToHexString(payloads[1])), mode, targets);
        });

    private Task<FanRpmReadings> ReadTachAsync(State state, CancellationToken token) =>
        WithActorAsync(state, false, token, (session, context, ignoredTimestamp) => ReadSpeedsAsync(session, context, 0x88, state, token));

    private async Task<FanRpmReadings> ReadSpeedsAsync(IRazerCommandSession session,
        ValidationRunContext context, byte id, State state, CancellationToken token)
    {
        int[] rpm = new int[2];
        for (byte zone = 1; zone <= 2; zone++)
        {
            FanRpmSelector selector = (FanRpmSelector)zone;
            var result = await ExecuteAsync(session, context, new(0x0D, id, [1, zone, 0], 3,
                p => FanRpmProtocol.TryDecodeQuery(p.Span, selector, out _)), token);
            if (result.Failure is not null || !FanRpmProtocol.TryDecodeQuery(result.Value.Span, selector, out rpm[zone - 1]))
            {
                throw new Stop(id == 0x81 ? "TargetReadAmbiguous" : "TachReadAmbiguous", state.Writes > 0);
            }
        }
        return new(rpm[0], rpm[1]);
    }

    private async Task<T> WithActorAsync<T>(State state, bool restoring, CancellationToken token,
        Func<IRazerCommandSession, ValidationRunContext, long, Task<T>> operation)
    {
        token.ThrowIfCancellationRequested();
        // Each unit owns one actor and fresh transaction space. Failed retirement
        // forbids reopening, so bounded cleanup never creates overlapping ownership.
        var context = new ValidationRunContext();
        var opened = await actors.OpenAsync(context);
        ValidationActor? actor = opened.Value;
        try
        {
            if (opened.Failure is not null || actor is null)
            {
                throw new Stop("OpenFailed", state.Writes > 0);
            }

            string? failure = await admission(restoring, token);
            if (failure is not null)
            {
                throw new Stop("SafetyAdmissionLost:" + failure, knownSafetyLoss: true);
            }

            long adapterTimestamp = timeProvider.GetTimestamp();
            if (!context.TryAllocateTransaction(out byte id))
            {
                throw new Stop("TransactionExhausted");
            }

            var adapter = await new AdapterPowerCommandClient(actor.Session, exchangeTimeout).QueryAsync(id, token);
            if (adapter.Failure is not null || !HyperBoostValidationPower.IsAdmittedFullPower(adapter.Value)
                || timeProvider.GetElapsedTime(adapterTimestamp) > MaximumAdmissionAge)
            {
                throw new Stop("FullAdapterPowerNotConfirmed", state.Writes > 0);
            }

            if (!submissionAllowed(restoring))
            {
                throw new Stop("SafetyAdmissionLost:StaleOrUnsafeAdmission", knownSafetyLoss: true);
            }

            return await operation(actor.Session, context, adapterTimestamp);
        }
        finally
        {
            if (actor is not null)
            {
                await BoundedValidationActorDisposal.DisposeAsync(actor, timeProvider, cleanupDrainTimeout, PendingDisposalRegistry.Shared);
                if (actor.Session.Disposition != RazerCommandSessionDisposition.FullyDisposed)
                {
                    throw new Stop("ActorRetirementUnconfirmed", state.Writes > 0);
                }
            }
        }
    }

    private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(IRazerCommandSession session,
        ValidationRunContext context, RazerCommandSpec command, CancellationToken token)
    {
        if (!context.TryAllocateTransaction(out byte id))
        {
            throw new Stop("TransactionExhausted");
        }

        return await session.ExecuteAsync(command, () => TransactionIdAssignment.Valid(id), exchangeTimeout, token);
    }

    private void Journal(string stage, bool restoring, RazerCommandDefinition? command, State state)
    {
        try
        {
            journal(new(stage, timeProvider.GetUtcNow(), restoring,
            command is null ? null : Convert.ToHexString(command.Payload.AsSpan()), state.Result()));
        }
        catch { throw new Stop("JournalFailed", state.Writes > 0); }
    }

    private void ValidateTemplates()
    {
        for (byte zone = 1; zone <= 2; zone++)
        {
            Check(zone == 1 ? ProductionBatteryPort.FanMaximumStateFirstAction : ProductionBatteryPort.FanMaximumStateSecondAction,
                2, [1, zone, 0, 1]);
            Check(zone == 1 ? ProductionBatteryPort.FanMaximumSpeedFirstAction : ProductionBatteryPort.FanMaximumSpeedSecondAction,
                1, [1, zone, 0x36]);
        }
        void Check(string action, byte id, byte[] expected)
        {
            if (!catalog.TryGet(action, out var template) || template is null || template.CommandClass != 0x0D
                || template.CommandId != id || template.OneShotOnly || !template.Payload.AsSpan().SequenceEqual(expected)
                || !template.ExpectedResponsePayload.AsSpan().SequenceEqual(expected))
            {
                throw new Stop("CapturedTemplateMismatch");
            }
        }
    }

    private static RazerCommandDefinition Mode(byte zone, bool manual)
    {
        // Pinned Product736 policy module 50779 profile 1, main 3327 P5=5;
        // base setThermalMode encodes [class, zone, mode, manual].
        byte[] payload = [1, zone, 5, manual ? (byte)1 : (byte)0];
        return new(0x0D, 2, payload, payload);
    }
    private static RazerCommandDefinition Speed(byte zone, int rpm)
    {
        byte[] payload = [1, zone, checked((byte)(rpm / 100))];
        return new(0x0D, 1, payload, payload);
    }
    private static bool Restorable(int rpm) => rpm is >= 2000 and <= 5400 && rpm % 100 == 0;
    private static bool InRange(int value, int target) => value > 0 && Math.Abs(value - target) <= 200;
    private static bool StableLastThree(List<LowFanValidationSample> samples)
    {
        var last = samples.TakeLast(3).Select(x => x.Rpm).ToArray();
        return last.Length == 3 && last.Max(x => x.Selector1Rpm) - last.Min(x => x.Selector1Rpm) <= 100
            && last.Max(x => x.Selector2Rpm) - last.Min(x => x.Selector2Rpm) <= 100;
    }
    private sealed record StateRead(PerformanceFanThermalPair Pair, ThermalModeState Mode, FanRpmReadings Targets);
    private sealed class Stop(string message, bool ambiguous = false, bool knownSafetyLoss = false) : Exception(message)
    {
        internal bool Ambiguous { get; } = ambiguous;
        internal bool KnownSafetyLoss { get; } = knownSafetyLoss;
    }
    private sealed class State(SilentLowFanScenario scenario)
    {
        internal SilentLowFanScenario Scenario { get; } = scenario;
        internal string? Failure;
        internal bool Ambiguous, Restored;
        internal int Writes;
        internal int? Lowest;
        internal PerformanceFanThermalPair? Baseline, Restoration;
        internal FanRpmReadings? BaselineTargets, BaselineFanRpm, RestorationTargets;
        internal SilentLowFanStep? Preparation;
        internal List<SilentLowFanStep> Steps { get; } = [];
        internal SilentLowFanValidationResult Result() => new(Failure is null && Restored, Restored,
            Writes > 0 && !Restored, Failure, Writes, Ambiguous, Scenario, Baseline, BaselineTargets,
            BaselineFanRpm, Preparation, Steps.ToArray(), Lowest, Restoration, RestorationTargets);
    }
}
