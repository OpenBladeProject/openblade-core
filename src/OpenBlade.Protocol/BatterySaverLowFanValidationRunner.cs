namespace OpenBlade.Protocol;

internal sealed record LowFanValidationSample(
    int Number,
    DateTimeOffset Timestamp,
    FanRpmReadings Rpm);

internal sealed record BatterySaverLowFanValidationResult(
    bool Success,
    bool Restored,
    bool ManualRecoveryRequired,
    string? Failure,
    int WritesAttempted,
    int SafetyChecks,
    bool IndeterminatePartialWrite,
    PerformanceFanThermalPair? Baseline,
    FanRpmReadings? BaselineTargets,
    FanRpmReadings? BaselineFanRpm,
    PerformanceFanThermalPair? Candidate,
    FanRpmReadings? CandidateTargets,
    PerformanceFanThermalPair? Restoration,
    FanRpmReadings? RestorationTargets,
    IReadOnlyList<LowFanValidationSample> Samples);

internal sealed record LowFanValidationProgress(
    string Stage,
    DateTimeOffset Timestamp,
    bool Restoring = false,
    byte? CommandClass = null,
    byte? CommandId = null,
    string? PayloadHex = null,
    BatterySaverLowFanValidationResult? State = null);

/// <summary>
/// One developer-only, source-reviewed 1500 RPM experiment. This deliberately does
/// not use or expand the production fan range. The caller admits only the approved
/// exact host, BIOS and attended battery session before every write and sample.
/// </summary>
internal sealed class BatterySaverLowFanValidationRunner
{
    private const int CandidateRpm = 1500;
    private const int RequiredConsecutiveSamples = 3;
    private const int MaximumSamples = 30;
    // Product736 getPerformanceFanSpeedProfile defaults to thermal class 1.
    private const byte ThermalClass = 0x01;
    private readonly IValidationActorFactory actors;
    private readonly DeviceCommandCatalog catalog;
    private readonly Func<bool, CancellationToken, Task<string?>> admission;
    private readonly Action<LowFanValidationProgress> journal;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly TimeSpan sampleInterval;
    private readonly int maximumSamples;
    private readonly bool honorFanInterCommandTiming;
    private readonly Func<bool, bool>? submissionAllowed;
    private int started;

    internal BatterySaverLowFanValidationRunner(
        IValidationActorFactory actors,
        DeviceCommandCatalog catalog,
        Func<bool, CancellationToken, Task<string?>> admission,
        Action<LowFanValidationProgress> journal,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        TimeSpan? sampleInterval = null,
        int maximumSamples = MaximumSamples,
        bool honorFanInterCommandTiming = true,
        Func<bool, bool>? submissionAllowed = null)
    {
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1);
        if (this.sampleInterval < TimeSpan.Zero || this.sampleInterval > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        }
        if (maximumSamples is < RequiredConsecutiveSamples or > MaximumSamples)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        }
        this.maximumSamples = maximumSamples;
        this.honorFanInterCommandTiming = honorFanInterCommandTiming;
        this.submissionAllowed = submissionAllowed;
    }

    internal async Task<BatterySaverLowFanValidationResult> RunAsync(
        CancellationToken cancellationToken)
    {
        var state = new RunState();
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return state.Fail("AlreadyRun");
        }
        ValidationActor? actor = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Validate every captured template, including its echo contract, before
            // opening the device. The experiment substitutes only the reviewed fields.
            ValidateTemplates();
            var context = new ValidationRunContext();
            HidSessionResult<ValidationActor> opened = await actors.OpenAsync(context)
                .ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                return state.Fail("OpenFailed");
            }
            IRazerCommandSession session = actor.Session;
            if (!await AdmitAsync(false, state, cancellationToken).ConfigureAwait(false))
            {
                return state.ToResult();
            }
            PairRead? first = await ReadPairAsync(context, session, cancellationToken)
                .ConfigureAwait(false);
            PairRead? second = await ReadPairAsync(context, session, cancellationToken)
                .ConfigureAwait(false);
            state.Baseline = first?.Pair;
            if (first is null || second is null)
            {
                return state.Fail("BaselineReadFailed");
            }
            if (first.Mode != ThermalModeState.BatterySaverAutomatic
                || second.Mode != ThermalModeState.BatterySaverAutomatic
                || first.Pair != second.Pair)
            {
                return state.Fail("BaselineMustBeStableBatterySaverAutomatic");
            }
            state.BaselineTargets = await ReadSpeedsAsync(context, session, 0x81, cancellationToken)
                .ConfigureAwait(false);
            if (state.BaselineTargets is not { } baselineTargets
                || !RestorableTarget(baselineTargets.Selector1Rpm)
                || !RestorableTarget(baselineTargets.Selector2Rpm))
            {
                return state.Fail("BaselineTargetsNotRestorable");
            }
            state.BaselineFanRpm = await ReadSpeedsAsync(context, session, 0x88, cancellationToken)
                .ConfigureAwait(false);
            if (state.BaselineFanRpm is null)
            {
                return state.Fail("BaselineTachReadFailed");
            }
            if (!Journal("Baseline", false, null, state))
            {
                return state.ToResult();
            }
            RazerCommandDefinition[] candidate =
            [
                ModeCommand(1, manual: true), ModeCommand(2, manual: true),
                SpeedCommand(1, CandidateRpm), SpeedCommand(2, CandidateRpm),
            ];
            for (int index = 0; index < candidate.Length; index++)
            {
                if (!await WriteAsync(context, session, candidate[index], false, state,
                    cancellationToken).ConfigureAwait(false))
                {
                    return state.ToResult();
                }
                if (index < candidate.Length - 1)
                {
                    await DelayAsync(ThermalWriteTiming.MaximumInterCommandDelay(index),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            PairRead? confirmed = await ReadPairAsync(context, session, cancellationToken)
                .ConfigureAwait(false);
            state.Candidate = confirmed?.Pair;
            if (confirmed?.Mode != ThermalModeState.BatterySaverManual)
            {
                return state.Fail("CandidateModeReadAmbiguous", ambiguous: true);
            }
            state.CandidateTargets = await ReadSpeedsAsync(context, session, 0x81, cancellationToken)
                .ConfigureAwait(false);
            if (state.CandidateTargets is null)
            {
                return state.Fail("CandidateTargetsReadFailed", ambiguous: true);
            }
            bool storedTargetConfirmed = state.CandidateTargets == new FanRpmReadings(CandidateRpm, CandidateRpm);
            if (!storedTargetConfirmed)
            {
                state.Failure = "StoredTargetClamped";
            }
            int consecutive = 0;
            for (int index = 0; index < maximumSamples; index++)
            {
                if (sampleInterval > TimeSpan.Zero)
                {
                    await Task.Delay(sampleInterval, timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (!await AdmitAsync(false, state, cancellationToken).ConfigureAwait(false))
                {
                    break; // A known safety loss may restore only under a fresh restoration admission.
                }
                FanRpmReadings? rpm = await ReadSpeedsAsync(context, session, 0x88, cancellationToken)
                    .ConfigureAwait(false);
                if (rpm is null)
                {
                    return state.Fail("CandidateTachReadFailed", ambiguous: true);
                }
                state.Samples.Add(new(index + 1, timeProvider.GetUtcNow(), rpm));
                if (!Journal("Sample", false, null, state))
                {
                    return state.ToResult();
                }
                consecutive = InCandidateRange(rpm.Selector1Rpm) && InCandidateRange(rpm.Selector2Rpm)
                    ? consecutive + 1 : 0;
                if (consecutive >= RequiredConsecutiveSamples)
                {
                    break;
                }
            }
            if (state.Failure is null && consecutive < RequiredConsecutiveSamples)
            {
                state.Failure = "ActualRpmDidNotSettleAt1500";
            }
            // Independent GETs must still establish a known manual state immediately
            // before restoration. ACKs alone never permit another command sequence.
            PairRead? restorePrecondition = await ReadPairAsync(context, session, cancellationToken)
                .ConfigureAwait(false);
            FanRpmReadings? targetsBeforeRestore = await ReadSpeedsAsync(
                context, session, 0x81, cancellationToken).ConfigureAwait(false);
            if (restorePrecondition?.Mode != ThermalModeState.BatterySaverManual
                || targetsBeforeRestore is null)
            {
                return state.Fail("RestorePreconditionReadAmbiguous", ambiguous: true);
            }
            RazerCommandDefinition[] restore =
            [
                SpeedCommand(1, baselineTargets.Selector1Rpm),
                SpeedCommand(2, baselineTargets.Selector2Rpm),
                ModeCommand(1, manual: false), ModeCommand(2, manual: false),
            ];
            for (int index = 0; index < restore.Length; index++)
            {
                if (!await WriteAsync(context, session, restore[index], true, state,
                    cancellationToken).ConfigureAwait(false))
                {
                    return state.ToResult();
                }
                TimeSpan delay = index switch
                {
                    0 => ThermalWriteTiming.MaximumInterCommandDelay(2),
                    2 => ThermalWriteTiming.MaximumInterCommandDelay(0),
                    _ => TimeSpan.Zero,
                };
                await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            PairRead? restored = await ReadPairAsync(context, session, cancellationToken)
                .ConfigureAwait(false);
            state.Restoration = restored?.Pair;
            state.RestorationTargets = await ReadSpeedsAsync(context, session, 0x81, cancellationToken)
                .ConfigureAwait(false);
            if (restored?.Mode != ThermalModeState.BatterySaverAutomatic
                || restored.Pair != state.Baseline || state.RestorationTargets != baselineTargets)
            {
                return state.Fail("RestorationNotConfirmed", ambiguous: true);
            }
            state.Restored = true;
            Journal("Restored", true, null, state);
            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return state.Fail("Canceled", ambiguous: state.WritesAttempted > 0);
        }
        catch (Exception exception)
        {
            return state.Fail($"UnexpectedFailure:{exception.GetType().Name}",
                ambiguous: state.WritesAttempted > 0);
        }
        finally
        {
            if (actor is not null)
            {
                await BoundedValidationActorDisposal.DisposeAsync(actor, timeProvider,
                    cleanupDrainTimeout, PendingDisposalRegistry.Shared).ConfigureAwait(false);
            }
        }
    }

    private static bool RestorableTarget(int rpm) => rpm is >= 2000 and <= 5400 && rpm % 100 == 0;
    private static bool InCandidateRange(int rpm) => rpm is >= 1300 and <= 1700;

    private void ValidateTemplates()
    {
        for (byte zone = 1; zone <= 2; zone++)
        {
            string stateAction = zone == 1 ? ProductionBatteryPort.FanMaximumStateFirstAction
                : ProductionBatteryPort.FanMaximumStateSecondAction;
            string speedAction = zone == 1 ? ProductionBatteryPort.FanMaximumSpeedFirstAction
                : ProductionBatteryPort.FanMaximumSpeedSecondAction;
            ValidateTemplate(stateAction, 0x02, [ThermalClass, zone, 0x00, 0x01]);
            ValidateTemplate(speedAction, 0x01, [ThermalClass, zone, 0x36]);
        }
    }

    private void ValidateTemplate(string action, byte id, byte[] expected)
    {
        if (!catalog.TryGet(action, out RazerCommandDefinition? template) || template is null
            || template.CommandClass != ThermalModeProtocol.CommandClass || template.CommandId != id
            || template.OneShotOnly || !template.Payload.AsSpan().SequenceEqual(expected)
            || !template.ExpectedResponsePayload.AsSpan().SequenceEqual(expected))
        {
            throw new UnvalidatedCommandException($"Captured template '{action}' does not match the exact 0581 contract.");
        }
    }

    private static RazerCommandDefinition ModeCommand(byte zone, bool manual)
    {
        byte[] payload = [ThermalClass, zone, 0x03, manual ? (byte)1 : (byte)0];
        return new(ThermalModeProtocol.CommandClass, 0x02, payload, payload);
    }

    private static RazerCommandDefinition SpeedCommand(byte zone, int rpm)
    {
        byte[] payload = [ThermalClass, zone, checked((byte)(rpm / 100))];
        return new(ThermalModeProtocol.CommandClass, 0x01, payload, payload);
    }

    private async Task<bool> AdmitAsync(bool restoring, RunState state, CancellationToken cancellationToken)
    {
        state.SafetyChecks++;
        string? failure = await admission(restoring, cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            return true;
        }
        state.Fail($"SafetyAdmissionLost:{failure}");
        return false;
    }

    private bool Journal(string stage, bool restoring, RazerCommandDefinition? command, RunState state)
    {
        try
        {
            journal(new(stage, timeProvider.GetUtcNow(), restoring, command?.CommandClass,
                command?.CommandId, command is null ? null : Convert.ToHexString(command.Payload.AsSpan()),
                state.ToResult()));
            return true;
        }
        catch
        {
            state.Fail("JournalFailed");
            return false;
        }
    }

    private async Task<bool> WriteAsync(ValidationRunContext context, IRazerCommandSession session,
        RazerCommandDefinition command, bool restoring, RunState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await AdmitAsync(restoring, state, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        // Durable intent must succeed before the native submission is even attempted.
        if (!Journal("WriteIntent", restoring, command, state))
        {
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        state.WritesAttempted++;
        HidSessionResult<ReadOnlyMemory<byte>> write = await ExecuteAsync(context, session,
            new RazerCommandSpec(command.CommandClass, command.CommandId, command.Payload.ToArray(),
                command.ExpectedResponsePayload.Length,
                response => response.Span.SequenceEqual(command.ExpectedResponsePayload.AsSpan()))
            {
                // The native I/O boundary must recheck the synchronous admission
                // after durable journaling and any time spent waiting in the actor.
                SubmissionAllowed = submissionAllowed is null ? null : () => submissionAllowed(restoring),
            },
            cancellationToken).ConfigureAwait(false);
        if (write.Failure is not null || !write.Value.Span.SequenceEqual(command.ExpectedResponsePayload.AsSpan()))
        {
            state.Fail(restoring ? "RestoreWriteIndeterminate" : "CandidateWriteIndeterminate", ambiguous: true);
            return false;
        }
        return Journal("WriteAcknowledged", restoring, command, state);
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        honorFanInterCommandTiming && delay > TimeSpan.Zero
            ? Task.Delay(delay, timeProvider, cancellationToken) : Task.CompletedTask;

    private async Task<PairRead?> ReadPairAsync(ValidationRunContext context,
        IRazerCommandSession session, CancellationToken cancellationToken)
    {
        byte[][] payloads = new byte[2][];
        for (byte zone = 1; zone <= 2; zone++)
        {
            ThermalChannel channel = (ThermalChannel)zone;
            HidSessionResult<ReadOnlyMemory<byte>> read = await ExecuteAsync(context, session,
                new RazerCommandSpec(ThermalModeProtocol.CommandClass, ThermalModeProtocol.QueryCommandId,
                    ThermalModeProtocol.CreateQueryPayload(channel), 4,
                    payload => ThermalModeProtocol.TryDecodeQuery(payload.Span, channel, out _)),
                cancellationToken).ConfigureAwait(false);
            if (read.Failure is not null)
            {
                return null;
            }
            payloads[zone - 1] = read.Value.ToArray();
        }
        return ThermalModeProtocol.TryDecodeMatchingPair(payloads[0], payloads[1], out ThermalModeState mode)
            ? new(new(Convert.ToHexString(payloads[0]), Convert.ToHexString(payloads[1])), mode) : null;
    }

    private async Task<FanRpmReadings?> ReadSpeedsAsync(ValidationRunContext context,
        IRazerCommandSession session, byte commandId, CancellationToken cancellationToken)
    {
        int[] rpm = new int[2];
        for (byte zone = 1; zone <= 2; zone++)
        {
            FanRpmSelector selector = (FanRpmSelector)zone;
            HidSessionResult<ReadOnlyMemory<byte>> read = await ExecuteAsync(context, session,
                new RazerCommandSpec(ThermalModeProtocol.CommandClass, commandId, [ThermalClass, zone, 0], 3,
                    payload => FanRpmProtocol.TryDecodeQuery(payload.Span, selector, out _)),
                cancellationToken).ConfigureAwait(false);
            if (read.Failure is not null
                || !FanRpmProtocol.TryDecodeQuery(read.Value.Span, selector, out rpm[zone - 1]))
            {
                return null;
            }
        }
        return new(rpm[0], rpm[1]);
    }

    private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(ValidationRunContext context,
        IRazerCommandSession session, RazerCommandSpec command, CancellationToken cancellationToken)
    {
        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            return new(default, HidSessionFailureKind.Exhausted);
        }
        return await session.ExecuteAsync(command, () => TransactionIdAssignment.Valid(transactionId),
            exchangeTimeout, cancellationToken).ConfigureAwait(false);
    }

    private sealed record PairRead(PerformanceFanThermalPair Pair, ThermalModeState Mode);

    private sealed class RunState
    {
        internal string? Failure { get; set; }
        internal bool Restored { get; set; }
        internal bool IndeterminatePartialWrite { get; set; }
        internal int WritesAttempted { get; set; }
        internal int SafetyChecks { get; set; }
        internal PerformanceFanThermalPair? Baseline { get; set; }
        internal FanRpmReadings? BaselineTargets { get; set; }
        internal FanRpmReadings? BaselineFanRpm { get; set; }
        internal PerformanceFanThermalPair? Candidate { get; set; }
        internal FanRpmReadings? CandidateTargets { get; set; }
        internal PerformanceFanThermalPair? Restoration { get; set; }
        internal FanRpmReadings? RestorationTargets { get; set; }
        internal List<LowFanValidationSample> Samples { get; } = [];

        internal BatterySaverLowFanValidationResult Fail(string failure, bool ambiguous = false)
        {
            Failure = failure;
            IndeterminatePartialWrite |= ambiguous;
            return ToResult();
        }

        internal BatterySaverLowFanValidationResult ToResult() => new(
            Failure is null && Restored, Restored, WritesAttempted > 0 && !Restored,
            Failure, WritesAttempted, SafetyChecks, IndeterminatePartialWrite,
            Baseline, BaselineTargets, BaselineFanRpm, Candidate, CandidateTargets,
            Restoration, RestorationTargets, Samples.ToArray());
    }
}
