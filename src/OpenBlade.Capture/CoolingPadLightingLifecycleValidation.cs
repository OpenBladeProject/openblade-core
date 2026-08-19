using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadLightingValidationStage
{
    UniformFrame,
    Blackout,
    BrightnessRestored,
}

internal enum CoolingPadLightingValidationFailure
{
    None,
    Canceled,
    OpenFailed,
    SetOnlyUnavailable,
    TransactionBudgetUnavailable,
    BrightnessQueryFailed,
    BaselineBrightnessZero,
    FrameSubmissionIndeterminate,
    UniformFrameNotConfirmed,
    BlackoutWriteIndeterminate,
    BlackoutNotConfirmed,
    BrightnessRestoreIndeterminate,
    BrightnessRestoreNotConfirmed,
    UnexpectedFailure,
}

internal sealed record CoolingPadLightingLifecycleValidationResult(
    byte? BaselineBrightnessRaw,
    byte? BaselineBrightnessPercent,
    bool FrameSubmissionAttempted,
    bool FrameSubmissionCompleted,
    bool UniformFramePhysicallyConfirmed,
    bool BlackoutAttempted,
    bool BlackoutAcknowledged,
    bool BlackoutPhysicallyConfirmed,
    bool BrightnessRestoreAttempted,
    bool BrightnessRestoreAcknowledged,
    bool BrightnessRestorePhysicallyConfirmed,
    bool ManualPresetRestoreRequired,
    bool ManualRecoveryRequired,
    CoolingPadLightingValidationFailure Failure)
{
    internal bool Success => Failure == CoolingPadLightingValidationFailure.None
        && BaselineBrightnessRaw is > 0
        && FrameSubmissionCompleted
        && UniformFramePhysicallyConfirmed
        && BlackoutAcknowledged
        && BlackoutPhysicallyConfirmed
        && BrightnessRestoreAcknowledged
        && BrightnessRestorePhysicallyConfirmed;
}

internal sealed class CoolingPadLightingLifecycleValidationRunner
{
    internal static readonly RgbColor ValidationColor = new(0x12, 0x34, 0x56);

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadLightingLifecycleValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider? timeProvider = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadLightingLifecycleValidationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadLightingLifecycleValidationResult> RunAsync(
        Func<CoolingPadLightingValidationStage, CancellationToken, Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            return Empty(CoolingPadLightingValidationFailure.UnexpectedFailure);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Empty(CoolingPadLightingValidationFailure.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            return Empty(CoolingPadLightingValidationFailure.UnexpectedFailure);
        }

        HidSessionResult<ValidationActor> opened =
            await actorFactory.OpenAsync(context).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return Empty(cancellationToken.IsCancellationRequested
                ? CoolingPadLightingValidationFailure.Canceled
                : CoolingPadLightingValidationFailure.OpenFailed);
        }

        ValidationActor actor = opened.Value;
        var state = new MutableResult();
        try
        {
            if (actor.Session is not IRazerSetFeatureCommandSession submitter)
            {
                state.Failure = CoolingPadLightingValidationFailure.SetOnlyUnavailable;
                return state.ToResult();
            }
            if (!context.TryAllocateTransaction(out byte queryTransaction)
                || !context.TryAllocateTransaction(out byte frameTransaction)
                || !context.TryAllocateTransaction(out byte blackoutTransaction)
                || !context.TryAllocateTransaction(out byte restoreTransaction))
            {
                state.Failure =
                    CoolingPadLightingValidationFailure.TransactionBudgetUnavailable;
                return state.ToResult();
            }

            HidSessionResult<ReadOnlyMemory<byte>> query = await actor.Session.ExecuteAsync(
                    CoolingPadLightingProtocol.CreateBrightnessQueryCommand(),
                    () => TransactionIdAssignment.Valid(queryTransaction),
                    ExchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (query.Failure is not null
                || !CoolingPadLightingProtocol.TryDecodeBrightnessQuery(
                    query.Value.Span,
                    out CoolingPadBrightnessReading baseline))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingValidationFailure.Canceled
                    : CoolingPadLightingValidationFailure.BrightnessQueryFailed;
                return state.ToResult();
            }

            state.BaselineBrightnessRaw = baseline.Raw;
            state.BaselineBrightnessPercent = baseline.Percent;
            if (baseline.Raw == 0)
            {
                state.Failure = CoolingPadLightingValidationFailure.BaselineBrightnessZero;
                return state.ToResult();
            }

            var frame = new CoolingPadLightingFrame(
                Enumerable.Repeat(
                    ValidationColor,
                    CoolingPadLightingFrame.SlotCount).ToArray());
            state.FrameSubmissionAttempted = true;
            HidSessionResult<ReadOnlyMemory<byte>> frameResult = await submitter.SubmitAsync(
                    CoolingPadLightingProtocol.CreateFrameSetOnlyCommand(frame),
                    () => TransactionIdAssignment.Valid(frameTransaction),
                    ExchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (frameResult.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure =
                    CoolingPadLightingValidationFailure.FrameSubmissionIndeterminate;
                return state.ToResult();
            }
            state.FrameSubmissionCompleted = true;
            state.ManualPresetRestoreRequired = true;

            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadLightingValidationStage.UniformFrame,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingValidationFailure.Canceled
                    : CoolingPadLightingValidationFailure.UniformFrameNotConfirmed;
                return state.ToResult();
            }
            state.UniformFramePhysicallyConfirmed = true;

            state.BlackoutAttempted = true;
            HidSessionResult<ReadOnlyMemory<byte>> blackout = await actor.Session.ExecuteAsync(
                    CoolingPadLightingProtocol.CreateBrightnessSetCommand(0),
                    () => TransactionIdAssignment.Valid(blackoutTransaction),
                    ExchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (blackout.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure =
                    CoolingPadLightingValidationFailure.BlackoutWriteIndeterminate;
                return state.ToResult();
            }
            state.BlackoutAcknowledged = true;

            bool blackoutConfirmed = await ConfirmAsync(
                    confirm,
                    CoolingPadLightingValidationStage.Blackout,
                    cancellationToken).ConfigureAwait(false);
            state.BlackoutPhysicallyConfirmed = blackoutConfirmed;

            state.BrightnessRestoreAttempted = true;
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            HidSessionResult<ReadOnlyMemory<byte>> restore = await actor.Session.ExecuteAsync(
                    CoolingPadLightingProtocol.CreateBrightnessRawSetCommand(
                        baseline.Raw),
                    () => TransactionIdAssignment.Valid(restoreTransaction),
                    ExchangeTimeout,
                    cleanup.Token)
                .ConfigureAwait(false);
            if (restore.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure =
                    CoolingPadLightingValidationFailure.BrightnessRestoreIndeterminate;
                return state.ToResult();
            }
            state.BrightnessRestoreAcknowledged = true;

            if (!blackoutConfirmed)
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingValidationFailure.Canceled
                    : CoolingPadLightingValidationFailure.BlackoutNotConfirmed;
                return state.ToResult();
            }

            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadLightingValidationStage.BrightnessRestored,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingValidationFailure.Canceled
                    : CoolingPadLightingValidationFailure
                        .BrightnessRestoreNotConfirmed;
                return state.ToResult();
            }
            state.BrightnessRestorePhysicallyConfirmed = true;
            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.ManualRecoveryRequired |= state.FrameSubmissionAttempted;
            state.Failure = CoolingPadLightingValidationFailure.Canceled;
            return state.ToResult();
        }
        catch
        {
            state.ManualRecoveryRequired |= state.FrameSubmissionAttempted;
            state.Failure = CoolingPadLightingValidationFailure.UnexpectedFailure;
            return state.ToResult();
        }
        finally
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                actor,
                timeProvider,
                CleanupTimeout,
                pendingDisposals).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ConfirmAsync(
        Func<CoolingPadLightingValidationStage, CancellationToken, Task<bool>> confirm,
        CoolingPadLightingValidationStage stage,
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

    private static CoolingPadLightingLifecycleValidationResult Empty(
        CoolingPadLightingValidationFailure failure) => new(
        BaselineBrightnessRaw: null,
        BaselineBrightnessPercent: null,
        FrameSubmissionAttempted: false,
        FrameSubmissionCompleted: false,
        UniformFramePhysicallyConfirmed: false,
        BlackoutAttempted: false,
        BlackoutAcknowledged: false,
        BlackoutPhysicallyConfirmed: false,
        BrightnessRestoreAttempted: false,
        BrightnessRestoreAcknowledged: false,
        BrightnessRestorePhysicallyConfirmed: false,
        ManualPresetRestoreRequired: false,
        ManualRecoveryRequired: false,
        failure);

    private sealed class MutableResult
    {
        internal byte? BaselineBrightnessRaw { get; set; }
        internal byte? BaselineBrightnessPercent { get; set; }
        internal bool FrameSubmissionAttempted { get; set; }
        internal bool FrameSubmissionCompleted { get; set; }
        internal bool UniformFramePhysicallyConfirmed { get; set; }
        internal bool BlackoutAttempted { get; set; }
        internal bool BlackoutAcknowledged { get; set; }
        internal bool BlackoutPhysicallyConfirmed { get; set; }
        internal bool BrightnessRestoreAttempted { get; set; }
        internal bool BrightnessRestoreAcknowledged { get; set; }
        internal bool BrightnessRestorePhysicallyConfirmed { get; set; }
        internal bool ManualPresetRestoreRequired { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal CoolingPadLightingValidationFailure Failure { get; set; }

        internal CoolingPadLightingLifecycleValidationResult ToResult() => new(
            BaselineBrightnessRaw,
            BaselineBrightnessPercent,
            FrameSubmissionAttempted,
            FrameSubmissionCompleted,
            UniformFramePhysicallyConfirmed,
            BlackoutAttempted,
            BlackoutAcknowledged,
            BlackoutPhysicallyConfirmed,
            BrightnessRestoreAttempted,
            BrightnessRestoreAcknowledged,
            BrightnessRestorePhysicallyConfirmed,
            ManualPresetRestoreRequired,
            ManualRecoveryRequired,
            Failure);
    }
}

internal enum CoolingPadLightingCadenceValidationStage
{
    CadenceStable,
    BrightnessRestored,
}

internal enum CoolingPadLightingCadenceValidationFailure
{
    None,
    Canceled,
    OpenFailed,
    SetOnlyUnavailable,
    TransactionBudgetUnavailable,
    BrightnessQueryFailed,
    BaselineBrightnessZero,
    FrameSubmissionIndeterminate,
    CadenceNotConfirmed,
    BrightnessRestoreIndeterminate,
    BrightnessRestoreNotConfirmed,
    UnexpectedFailure,
}

internal sealed record CoolingPadLightingCadenceValidationResult(
    byte? BaselineBrightnessRaw,
    int RequestedFrameCount,
    int SubmittedFrameCount,
    int MinimumFrameIntervalMilliseconds,
    double? ObservedAverageFramesPerSecond,
    bool CadenceTimingConfirmed,
    bool CadencePhysicallyConfirmed,
    bool BrightnessRestoreAttempted,
    bool BrightnessRestoreAcknowledged,
    bool BrightnessRestorePhysicallyConfirmed,
    bool ManualPresetRestoreRequired,
    bool ManualRecoveryRequired,
    CoolingPadLightingCadenceValidationFailure Failure)
{
    internal bool Success => Failure == CoolingPadLightingCadenceValidationFailure.None
        && BaselineBrightnessRaw is > 0
        && SubmittedFrameCount == RequestedFrameCount
        && CadenceTimingConfirmed
        && CadencePhysicallyConfirmed
        && BrightnessRestoreAcknowledged
        && BrightnessRestorePhysicallyConfirmed;
}

internal sealed class CoolingPadLightingCadenceValidationRunner
{
    internal const int ProductionFrameCount = 200;
    internal static readonly TimeSpan ProductionFrameInterval =
        TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly int frameCount;
    private readonly TimeSpan frameInterval;
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadLightingCadenceValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        int frameCount = ProductionFrameCount,
        TimeSpan? frameInterval = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        if (frameCount is < 2 or > 250)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.frameCount = frameCount;
        this.frameInterval = frameInterval ?? ProductionFrameInterval;
        if (this.frameInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameInterval));
        }
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadLightingCadenceValidationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadLightingCadenceValidationResult> RunAsync(
        Func<CoolingPadLightingCadenceValidationStage, CancellationToken, Task<bool>>
            confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref used, 1) != 0
            || cancellationToken.IsCancellationRequested)
        {
            return Empty(cancellationToken.IsCancellationRequested
                ? CoolingPadLightingCadenceValidationFailure.Canceled
                : CoolingPadLightingCadenceValidationFailure.UnexpectedFailure);
        }

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            return Empty(CoolingPadLightingCadenceValidationFailure.UnexpectedFailure);
        }

        HidSessionResult<ValidationActor> opened =
            await actorFactory.OpenAsync(context).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return Empty(cancellationToken.IsCancellationRequested
                ? CoolingPadLightingCadenceValidationFailure.Canceled
                : CoolingPadLightingCadenceValidationFailure.OpenFailed);
        }

        ValidationActor actor = opened.Value;
        var state = new CadenceMutableResult(frameCount, frameInterval);
        try
        {
            if (actor.Session is not IRazerSetFeatureCommandSession submitter)
            {
                state.Failure =
                    CoolingPadLightingCadenceValidationFailure.SetOnlyUnavailable;
                return state.ToResult();
            }

            if (!context.TryAllocateTransaction(out byte queryTransaction))
            {
                state.Failure = CoolingPadLightingCadenceValidationFailure
                    .TransactionBudgetUnavailable;
                return state.ToResult();
            }
            var frameTransactions = new byte[frameCount];
            for (int index = 0; index < frameTransactions.Length; index++)
            {
                if (!context.TryAllocateTransaction(out frameTransactions[index]))
                {
                    state.Failure = CoolingPadLightingCadenceValidationFailure
                        .TransactionBudgetUnavailable;
                    return state.ToResult();
                }
            }
            if (!context.TryAllocateTransaction(out byte restoreTransaction))
            {
                state.Failure = CoolingPadLightingCadenceValidationFailure
                    .TransactionBudgetUnavailable;
                return state.ToResult();
            }

            HidSessionResult<ReadOnlyMemory<byte>> query = await actor.Session.ExecuteAsync(
                    CoolingPadLightingProtocol.CreateBrightnessQueryCommand(),
                    () => TransactionIdAssignment.Valid(queryTransaction),
                    ExchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (query.Failure is not null
                || !CoolingPadLightingProtocol.TryDecodeBrightnessQuery(
                    query.Value.Span,
                    out CoolingPadBrightnessReading baseline))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadLightingCadenceValidationFailure.Canceled
                    : CoolingPadLightingCadenceValidationFailure.BrightnessQueryFailed;
                return state.ToResult();
            }
            state.BaselineBrightnessRaw = baseline.Raw;
            if (baseline.Raw == 0)
            {
                state.Failure =
                    CoolingPadLightingCadenceValidationFailure.BaselineBrightnessZero;
                return state.ToResult();
            }

            var frame = new CoolingPadLightingFrame(
                Enumerable.Repeat(
                    CoolingPadLightingLifecycleValidationRunner.ValidationColor,
                    CoolingPadLightingFrame.SlotCount).ToArray());
            long firstSubmission = 0;
            long lastSubmission = 0;
            long previousSubmission = 0;
            for (int index = 0; index < frameTransactions.Length; index++)
            {
                long started = timeProvider.GetTimestamp();
                firstSubmission = index == 0 ? started : firstSubmission;
                lastSubmission = started;
                if (index > 0
                    && timeProvider.GetElapsedTime(previousSubmission, started)
                        < frameInterval)
                {
                    state.CadenceTimingConfirmed = false;
                }
                previousSubmission = started;
                HidSessionResult<ReadOnlyMemory<byte>> submitted;
                try
                {
                    submitted = await submitter.SubmitAsync(
                            CoolingPadLightingProtocol.CreateFrameSetOnlyCommand(frame),
                            () => TransactionIdAssignment.Valid(frameTransactions[index]),
                            ExchangeTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    state.ManualRecoveryRequired = true;
                    state.Failure = CoolingPadLightingCadenceValidationFailure
                        .FrameSubmissionIndeterminate;
                    return state.ToResult();
                }
                if (submitted.Failure is not null)
                {
                    state.ManualRecoveryRequired = true;
                    state.Failure = CoolingPadLightingCadenceValidationFailure
                        .FrameSubmissionIndeterminate;
                    return state.ToResult();
                }

                state.SubmittedFrameCount++;
                state.ManualPresetRestoreRequired = true;
                if (index + 1 < frameTransactions.Length)
                {
                    try
                    {
                        await delay(frameInterval, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        state.Failure =
                            CoolingPadLightingCadenceValidationFailure.Canceled;
                        break;
                    }
                }
            }

            if (state.SubmittedFrameCount > 1)
            {
                double seconds = timeProvider.GetElapsedTime(
                    firstSubmission,
                    lastSubmission).TotalSeconds;
                if (seconds > 0)
                {
                    state.ObservedAverageFramesPerSecond =
                        (state.SubmittedFrameCount - 1) / seconds;
                }
            }

            if (state.Failure == CoolingPadLightingCadenceValidationFailure.None
                && !state.CadenceTimingConfirmed)
            {
                state.Failure =
                    CoolingPadLightingCadenceValidationFailure.CadenceNotConfirmed;
            }

            if (state.Failure == CoolingPadLightingCadenceValidationFailure.None)
            {
                try
                {
                    state.CadencePhysicallyConfirmed = await confirm(
                            CoolingPadLightingCadenceValidationStage.CadenceStable,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!state.CadencePhysicallyConfirmed)
                    {
                        state.Failure = cancellationToken.IsCancellationRequested
                            ? CoolingPadLightingCadenceValidationFailure.Canceled
                            : CoolingPadLightingCadenceValidationFailure
                                .CadenceNotConfirmed;
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    state.Failure = CoolingPadLightingCadenceValidationFailure.Canceled;
                }
                catch
                {
                    state.Failure =
                        CoolingPadLightingCadenceValidationFailure.UnexpectedFailure;
                }
            }

            state.BrightnessRestoreAttempted = true;
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            HidSessionResult<ReadOnlyMemory<byte>> restored =
                await actor.Session.ExecuteAsync(
                        CoolingPadLightingProtocol.CreateBrightnessRawSetCommand(
                            baseline.Raw),
                        () => TransactionIdAssignment.Valid(restoreTransaction),
                        ExchangeTimeout,
                        cleanup.Token)
                    .ConfigureAwait(false);
            if (restored.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = CoolingPadLightingCadenceValidationFailure
                    .BrightnessRestoreIndeterminate;
                return state.ToResult();
            }
            state.BrightnessRestoreAcknowledged = true;

            if (state.Failure == CoolingPadLightingCadenceValidationFailure.None)
            {
                state.BrightnessRestorePhysicallyConfirmed = await confirm(
                        CoolingPadLightingCadenceValidationStage.BrightnessRestored,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!state.BrightnessRestorePhysicallyConfirmed)
                {
                    state.Failure = cancellationToken.IsCancellationRequested
                        ? CoolingPadLightingCadenceValidationFailure.Canceled
                        : CoolingPadLightingCadenceValidationFailure
                            .BrightnessRestoreNotConfirmed;
                }
            }
            return state.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.ManualRecoveryRequired |= state.SubmittedFrameCount > 0;
            state.Failure = CoolingPadLightingCadenceValidationFailure.Canceled;
            return state.ToResult();
        }
        catch
        {
            state.ManualRecoveryRequired |= state.SubmittedFrameCount > 0;
            state.Failure = CoolingPadLightingCadenceValidationFailure.UnexpectedFailure;
            return state.ToResult();
        }
        finally
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                actor,
                timeProvider,
                CleanupTimeout,
                pendingDisposals).ConfigureAwait(false);
        }
    }

    private CoolingPadLightingCadenceValidationResult Empty(
        CoolingPadLightingCadenceValidationFailure failure) => new(
        BaselineBrightnessRaw: null,
        frameCount,
        SubmittedFrameCount: 0,
        MinimumFrameIntervalMilliseconds: (int)frameInterval.TotalMilliseconds,
        ObservedAverageFramesPerSecond: null,
        CadenceTimingConfirmed: false,
        CadencePhysicallyConfirmed: false,
        BrightnessRestoreAttempted: false,
        BrightnessRestoreAcknowledged: false,
        BrightnessRestorePhysicallyConfirmed: false,
        ManualPresetRestoreRequired: false,
        ManualRecoveryRequired: false,
        failure);

    private sealed class CadenceMutableResult(
        int requestedFrameCount,
        TimeSpan frameInterval)
    {
        internal byte? BaselineBrightnessRaw { get; set; }
        internal int SubmittedFrameCount { get; set; }
        internal double? ObservedAverageFramesPerSecond { get; set; }
        internal bool CadenceTimingConfirmed { get; set; } = true;
        internal bool CadencePhysicallyConfirmed { get; set; }
        internal bool BrightnessRestoreAttempted { get; set; }
        internal bool BrightnessRestoreAcknowledged { get; set; }
        internal bool BrightnessRestorePhysicallyConfirmed { get; set; }
        internal bool ManualPresetRestoreRequired { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal CoolingPadLightingCadenceValidationFailure Failure { get; set; }

        internal CoolingPadLightingCadenceValidationResult ToResult() => new(
            BaselineBrightnessRaw,
            requestedFrameCount,
            SubmittedFrameCount,
            (int)frameInterval.TotalMilliseconds,
            ObservedAverageFramesPerSecond,
            CadenceTimingConfirmed,
            CadencePhysicallyConfirmed,
            BrightnessRestoreAttempted,
            BrightnessRestoreAcknowledged,
            BrightnessRestorePhysicallyConfirmed,
            ManualPresetRestoreRequired,
            ManualRecoveryRequired,
            Failure);
    }
}

internal sealed class CoolingPadLightingCadenceValidationCommand(
    Func<Func<CoolingPadLightingCadenceValidationStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadLightingCadenceValidationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:LIGHTING-123456:20FPS:200-FRAMES:RESTORE-BRIGHTNESS:ONCE";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-lighting-cadence " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadLightingCadenceValidationResult result = await run(
                ConfirmAsync,
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
                uniformColor = "123456",
                requestedFramesPerSecond = 20,
                priorEffectReadbackAvailable = false,
                priorEffectHandbackValidated = false,
                frameCadenceValidated = false,
                productionAdmitted = false,
                result,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (result.SubmittedFrameCount > 0)
        {
            await output.WriteLineAsync(
                "Restore the prior lighting preset through Synapse or the physical lighting button. OpenBlade restored brightness only; it cannot read or restore the prior effect.")
                .ConfigureAwait(false);
        }
        return result.Success ? 0 : 2;
    }

    private async Task<bool> ConfirmAsync(
        CoolingPadLightingCadenceValidationStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadLightingCadenceValidationStage.CadenceStable =>
                "CADENCE-STABLE-NO-SLEEP-CONFIRMED",
            CoolingPadLightingCadenceValidationStage.BrightnessRestored =>
                "BRIGHTNESS-RESTORED-CONFIRMED",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        await output.WriteLineAsync(
            $"Type {expected} after physical confirmation.").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }
}

internal sealed class CoolingPadLightingLifecycleValidationCommand(
    Func<Func<CoolingPadLightingValidationStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadLightingLifecycleValidationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:LIGHTING-UNIFORM-123456:BLACKOUT:RESTORE-BRIGHTNESS:ONCE";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-lighting-lifecycle " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadLightingLifecycleValidationResult result = await run(
                ConfirmAsync,
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
                uniformColor = "123456",
                readOnly = false,
                productionAdmitted = false,
                priorEffectReadbackAvailable = false,
                priorEffectHandbackValidated = false,
                result,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (result.FrameSubmissionAttempted)
        {
            await output.WriteLineAsync(
                "Restore the prior lighting preset through Synapse or the physical lighting button. OpenBlade restored brightness only; it cannot read or restore the prior effect.")
                .ConfigureAwait(false);
        }
        return result.Success ? 0 : 2;
    }

    private async Task<bool> ConfirmAsync(
        CoolingPadLightingValidationStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadLightingValidationStage.UniformFrame =>
                "UNIFORM-123456-CONFIRMED",
            CoolingPadLightingValidationStage.Blackout => "BLACKOUT-CONFIRMED",
            CoolingPadLightingValidationStage.BrightnessRestored =>
                "BRIGHTNESS-RESTORED-CONFIRMED",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        await output.WriteLineAsync(
            $"Type {expected} after physical confirmation.").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }
}
