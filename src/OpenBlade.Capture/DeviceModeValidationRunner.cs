using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum DeviceModeValidationFailure
{
    None,
    Conflict,
    OpenFailed,
    BaselineReadFailed,
    BaselineNotNormal,
    ApplyFailed,
    CandidateReadFailed,
    CandidateMismatch,
    VisualCheckRejected,
    RestoreFailed,
    RestorationReadFailed,
    RestorationMismatch,
    Canceled,
}

internal sealed record DeviceModeValidationResult(
    string? BaselineMode,
    string? CandidateMode,
    string? RestoredMode,
    bool ApplyAttempted,
    bool ApplyAcknowledged,
    bool CandidateConfirmed,
    bool VisualCheckConfirmed,
    bool RestoreAttempted,
    bool RestoreAcknowledged,
    bool Restored,
    bool ManualRecoveryRequired,
    DeviceModeValidationFailure Failure)
{
    internal bool Success =>
        Failure == DeviceModeValidationFailure.None
        && ApplyAcknowledged
        && CandidateConfirmed
        && VisualCheckConfirmed
        && Restored;
}

internal interface IDeviceModeValidationRunner
{
    Task<DeviceModeValidationResult> RunAsync(
        Func<CancellationToken, Task<bool>> confirmVisualBehavior,
        CancellationToken cancellationToken);
}

internal sealed class DeviceModeValidationRunner : IDeviceModeValidationRunner
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    internal DeviceModeValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupTimeout = cleanupTimeout;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    public async Task<DeviceModeValidationResult> RunAsync(
        Func<CancellationToken, Task<bool>> confirmVisualBehavior,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmVisualBehavior);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Empty(DeviceModeValidationFailure.OpenFailed);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Empty(DeviceModeValidationFailure.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
        }
        catch
        {
            return Empty(DeviceModeValidationFailure.OpenFailed);
        }

        ValidationActor? actor = null;
        var state = new RunState();
        try
        {
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                state.Failure = opened.Failure == HidSessionFailureKind.ConflictBlocked
                    ? DeviceModeValidationFailure.Conflict
                    : DeviceModeValidationFailure.OpenFailed;
            }
            else
            {
                await RunOpenedAsync(
                    context,
                    actor.Session,
                    confirmVisualBehavior,
                    state,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Failure = DeviceModeValidationFailure.Canceled;
        }
        catch
        {
            state.Failure = DeviceModeValidationFailure.OpenFailed;
        }
        finally
        {
            if (actor is not null)
            {
                if (state.ApplyAttempted)
                {
                    await RestoreAsync(context, actor.Session, state).ConfigureAwait(false);
                }

                await BoundedValidationActorDisposal.DisposeAsync(
                    actor,
                    timeProvider,
                    cleanupTimeout,
                    pendingDisposals).ConfigureAwait(false);
            }
        }

        return state.ToResult();
    }

    private async Task RunOpenedAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        Func<CancellationToken, Task<bool>> confirmVisualBehavior,
        RunState state,
        CancellationToken cancellationToken)
    {
        ModeRead baseline = await QueryAsync(
            context,
            session,
            cancellationToken).ConfigureAwait(false);
        if (!baseline.Success)
        {
            state.Failure = DeviceModeValidationFailure.BaselineReadFailed;
            return;
        }

        state.Baseline = baseline.Mode;
        if (baseline.Mode != RazerDeviceMode.Normal)
        {
            state.Failure = DeviceModeValidationFailure.BaselineNotNormal;
            return;
        }

        state.ApplyAttempted = true;
        HidSessionResult<ReadOnlyMemory<byte>> apply = await SetAsync(
            context,
            session,
            RazerDeviceMode.Driver,
            cancellationToken).ConfigureAwait(false);
        if (apply.Failure is not null)
        {
            state.Failure = MapCancellation(
                apply.Failure,
                cancellationToken,
                DeviceModeValidationFailure.ApplyFailed);
            return;
        }

        state.ApplyAcknowledged = true;
        ModeRead candidate = await QueryAsync(
            context,
            session,
            cancellationToken).ConfigureAwait(false);
        if (!candidate.Success)
        {
            state.Failure = DeviceModeValidationFailure.CandidateReadFailed;
            return;
        }

        state.Candidate = candidate.Mode;
        if (candidate.Mode != RazerDeviceMode.Driver)
        {
            state.Failure = DeviceModeValidationFailure.CandidateMismatch;
            return;
        }

        state.CandidateConfirmed = true;
        state.VisualCheckConfirmed =
            await confirmVisualBehavior(cancellationToken).ConfigureAwait(false);
        if (!state.VisualCheckConfirmed)
        {
            state.Failure = DeviceModeValidationFailure.VisualCheckRejected;
        }
    }

    private async Task RestoreAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RunState state)
    {
        state.RestoreAttempted = true;
        HidSessionResult<ReadOnlyMemory<byte>> restore = await SetAsync(
            context,
            session,
            RazerDeviceMode.Normal,
            CancellationToken.None).ConfigureAwait(false);
        if (restore.Failure is not null)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = DeviceModeValidationFailure.RestoreFailed;
            return;
        }

        state.RestoreAcknowledged = true;
        ModeRead restored = await QueryAsync(
            context,
            session,
            CancellationToken.None).ConfigureAwait(false);
        if (!restored.Success)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = DeviceModeValidationFailure.RestorationReadFailed;
            return;
        }

        state.Restored = restored.Mode;
        if (restored.Mode != RazerDeviceMode.Normal)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = DeviceModeValidationFailure.RestorationMismatch;
            return;
        }

        state.RestorationConfirmed = true;
    }

    private async Task<ModeRead> QueryAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken)
    {
        var command = new RazerCommandSpec(
            DeviceModeProtocol.OperationClass,
            DeviceModeProtocol.QueryOperationId,
            DeviceModeProtocol.QueryArguments.ToArray(),
            expectedPayloadLength: 2,
            payload => DeviceModeProtocol.TryDecodeQuery(payload.Span, out _));
        HidSessionResult<ReadOnlyMemory<byte>> result = await ExecuteAsync(
            context,
            session,
            command,
            cancellationToken).ConfigureAwait(false);
        return result.Failure is null
            && DeviceModeProtocol.TryDecodeQuery(result.Value.Span, out RazerDeviceMode mode)
                ? new(true, mode)
                : new(false, default);
    }

    private Task<HidSessionResult<ReadOnlyMemory<byte>>> SetAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RazerDeviceMode mode,
        CancellationToken cancellationToken)
    {
        byte[] arguments = DeviceModeProtocol.CreateSetArguments(mode);
        return ExecuteAsync(
            context,
            session,
            new RazerCommandSpec(
                DeviceModeProtocol.OperationClass,
                DeviceModeProtocol.SetOperationId,
                arguments,
                expectedPayloadLength: 2,
                payload => DeviceModeProtocol.TryDecodeSet(payload.Span, mode)),
            cancellationToken);
    }

    private Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RazerCommandSpec command,
        CancellationToken cancellationToken)
    {
        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.Exhausted));
        }

        return session.ExecuteAsync(
            command,
            () => TransactionIdAssignment.Valid(transactionId),
            exchangeTimeout,
            cancellationToken);
    }

    private static DeviceModeValidationFailure MapCancellation(
        HidSessionFailureKind? failure,
        CancellationToken cancellationToken,
        DeviceModeValidationFailure fallback) =>
        failure == HidSessionFailureKind.Canceled || cancellationToken.IsCancellationRequested
            ? DeviceModeValidationFailure.Canceled
            : fallback;

    private static DeviceModeValidationResult Empty(DeviceModeValidationFailure failure) =>
        new(
            null,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            failure);

    private readonly record struct ModeRead(bool Success, RazerDeviceMode Mode);

    private sealed class RunState
    {
        internal RazerDeviceMode? Baseline { get; set; }
        internal RazerDeviceMode? Candidate { get; set; }
        internal RazerDeviceMode? Restored { get; set; }
        internal bool ApplyAttempted { get; set; }
        internal bool ApplyAcknowledged { get; set; }
        internal bool CandidateConfirmed { get; set; }
        internal bool VisualCheckConfirmed { get; set; }
        internal bool RestoreAttempted { get; set; }
        internal bool RestoreAcknowledged { get; set; }
        internal bool RestorationConfirmed { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal DeviceModeValidationFailure Failure { get; set; }

        internal DeviceModeValidationResult ToResult() => new(
            Baseline?.ToString(),
            Candidate?.ToString(),
            Restored?.ToString(),
            ApplyAttempted,
            ApplyAcknowledged,
            CandidateConfirmed,
            VisualCheckConfirmed,
            RestoreAttempted,
            RestoreAcknowledged,
            RestorationConfirmed,
            ManualRecoveryRequired,
            Failure);
    }
}
