namespace OpenBlade.Protocol;

public enum PerformanceFanValidationTarget
{
    Balanced,
    Silent,
    Performance,
    Fixed5400,
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
}

public sealed record PerformanceFanThermalPair(
    string Thermal1Hex,
    string Thermal2Hex);

public sealed record PerformanceFanSetterValidationResult(
    PerformanceFanValidationTarget Target,
    PerformanceFanThermalPair? Baseline,
    PerformanceFanThermalPair? Candidate,
    FanRpmReadings? CandidateFanRpm,
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
    public bool Success => Failure == PerformanceFanValidationFailure.None
        && ApplyAcknowledged
        && CandidateConfirmed
        && Restored;
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
    private static readonly TimeSpan DefaultFixedFanSettleDelay =
        TimeSpan.FromSeconds(3);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly TimeSpan fixedFanSettleDelay;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    internal PerformanceFanSetterValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry? pendingDisposals = null,
        TimeSpan? fixedFanSettleDelay = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.fixedFanSettleDelay = fixedFanSettleDelay
            ?? DefaultFixedFanSettleDelay;
        if (this.fixedFanSettleDelay < TimeSpan.Zero
            || this.fixedFanSettleDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(fixedFanSettleDelay));
        }

        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
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

            if (baseline.State == ExpectedState(target))
            {
                state.Failure = PerformanceFanValidationFailure.TargetAlreadyActive;
                return state.ToResult();
            }

            state.ApplyAttempted = true;
            foreach (byte[] payload in CreateApplyPayloads(target))
            {
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
            }

            state.ApplyAcknowledged = true;
            PairRead candidate = await ReadPairAsync(
                context, actor.Session, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
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
        return pairMatches
            ? new(
                responses[0],
                responses[1],
                new(Convert.ToHexString(responses[0]), Convert.ToHexString(responses[1])),
                state,
                null,
                false)
            : new(responses[0], responses[1], null, null, null, true);
    }

    private async Task<PerformanceFanValidationFailure> ValidateFixedFanRpmAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RunState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                fixedFanSettleDelay,
                timeProvider,
                cancellationToken).ConfigureAwait(false);

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
                    return MapCancellation(
                        response.Failure,
                        cancellationToken,
                        PerformanceFanValidationFailure.FixedFanRpmReadFailed);
                }
            }

            state.CandidateFanRpm = new(readings[0], readings[1]);

            // A 5400 RPM target has been observed settling near 5300 RPM. The
            // 5000-6000 band accommodates physical settling and 100 RPM protocol
            // units while still rejecting an unchanged low-speed fan response.
            return readings.All(rpm =>
                rpm >= FixedFanMinimumRpm && rpm <= FixedFanMaximumRpm)
                ? PerformanceFanValidationFailure.None
                : PerformanceFanValidationFailure.FixedFanRpmOutOfRange;
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
            _ => throw new InvalidOperationException(
                "Only a recognized automatic thermal state can be restored."),
        };

    private static ThermalModeState ExpectedState(
        PerformanceFanValidationTarget target) => target switch
        {
            PerformanceFanValidationTarget.Balanced => ThermalModeState.BalancedAutomatic,
            PerformanceFanValidationTarget.Silent => ThermalModeState.SilentAutomatic,
            PerformanceFanValidationTarget.Performance => ThermalModeState.PerformanceAutomatic,
            PerformanceFanValidationTarget.Fixed5400 => ThermalModeState.FixedMaximum,
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
        target, null, null, null, null, false, false, false, false, false, false,
        false, false, failure);

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
        internal PerformanceFanThermalPair? Restoration { get; set; }
        internal bool ApplyAttempted { get; set; }
        internal bool ApplyAcknowledged { get; set; }
        internal bool CandidateConfirmed { get; set; }
        internal bool RestoreAttempted { get; set; }
        internal bool RestoreAcknowledged { get; set; }
        internal bool Restored { get; set; }
        internal bool IndeterminatePartialWrite { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal PerformanceFanValidationFailure Failure { get; set; }

        internal PerformanceFanSetterValidationResult ToResult() => new(
            target,
            Baseline,
            Candidate,
            CandidateFanRpm,
            Restoration,
            ApplyAttempted,
            ApplyAcknowledged,
            CandidateConfirmed,
            RestoreAttempted,
            RestoreAcknowledged,
            Restored,
            IndeterminatePartialWrite,
            ManualRecoveryRequired,
            Failure);
    }
}
