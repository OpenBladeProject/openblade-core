namespace OpenBlade.Protocol;

internal interface ICoolingPadLightingExecutor : ICoolingPadLightingSink;

/// <summary>
/// Dormant typed executor for the captured cooling-pad lighting contract. It
/// shares the production accessory port's session, transaction allocator, and
/// operation gate, but no production composition exposes this surface while
/// lighting write admission remains closed.
/// </summary>
internal sealed class CoolingPadLightingExecutor : ICoolingPadLightingExecutor
{
    private readonly IRazerCommandSession session;
    private readonly ValidationRunContext context;
    private readonly SemaphoreSlim operationGate;
    private readonly TimeSpan exchangeTimeout;
    private readonly Func<bool> isUnavailable;
    private readonly Func<bool> isPoisoned;
    private readonly Action poison;

    internal CoolingPadLightingExecutor(
        IRazerCommandSession session,
        ValidationRunContext context,
        SemaphoreSlim operationGate,
        TimeSpan exchangeTimeout,
        Func<bool> isUnavailable,
        Func<bool> isPoisoned,
        Action poison)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.operationGate = operationGate
            ?? throw new ArgumentNullException(nameof(operationGate));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.isUnavailable = isUnavailable
            ?? throw new ArgumentNullException(nameof(isUnavailable));
        this.isPoisoned = isPoisoned
            ?? throw new ArgumentNullException(nameof(isPoisoned));
        this.poison = poison ?? throw new ArgumentNullException(nameof(poison));
    }

    public Task<CoolingPadLightingSinkResult> SubmitFrameAsync(
        CoolingPadLightingFrame frame,
        CancellationToken cancellationToken) => ExecuteWriteAsync(
            CoolingPadLightingProtocol.CreateFrameSetOnlyCommand(frame),
            setOnly: true,
            cancellationToken);

    public Task<CoolingPadLightingSinkResult> SetBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken) => ExecuteWriteAsync(
            CoolingPadLightingProtocol.CreateBrightnessSetCommand(percent),
            setOnly: false,
            cancellationToken);

    public Task<CoolingPadLightingSinkResult> SetBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken) => ExecuteWriteAsync(
            CoolingPadLightingProtocol.CreateBrightnessRawSetCommand(raw),
            setOnly: false,
            cancellationToken);

    public async Task<HidSessionResult<CoolingPadBrightnessReading>> QueryBrightnessAsync(
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isUnavailable())
            {
                return QueryFailure(HidSessionFailureKind.Disposed);
            }
            if (isPoisoned())
            {
                return QueryFailure(HidSessionFailureKind.Poisoned);
            }
            if (!context.TryAllocateTransaction(out byte transactionId))
            {
                return QueryFailure(HidSessionFailureKind.Exhausted);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RazerCommandSpec command =
                CoolingPadLightingProtocol.CreateBrightnessQueryCommand();
            HidSessionResult<ReadOnlyMemory<byte>> response = await session.ExecuteAsync(
                    command,
                    () => TransactionIdAssignment.Valid(transactionId),
                    exchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.Failure is { } failure)
            {
                return new(default, failure)
                {
                    Diagnostic = response.Diagnostic,
                };
            }
            if (!CoolingPadLightingProtocol.TryDecodeBrightnessQuery(
                    response.Value.Span,
                    out CoolingPadBrightnessReading reading))
            {
                return QueryFailure(HidSessionFailureKind.ProtocolFailure);
            }

            return new(reading, null);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<CoolingPadLightingSinkResult> ExecuteWriteAsync(
        RazerCommandSpec command,
        bool setOnly,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submissionPossible = false;
        try
        {
            if (isUnavailable() || isPoisoned())
            {
                return KnownNotApplied();
            }
            if (setOnly && session is not IRazerSetFeatureCommandSession)
            {
                return KnownNotApplied();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryAllocateTransaction(out byte transactionId))
            {
                return KnownNotApplied();
            }
            cancellationToken.ThrowIfCancellationRequested();

            submissionPossible = true;
            HidSessionResult<ReadOnlyMemory<byte>> response = setOnly
                ? await ((IRazerSetFeatureCommandSession)session).SubmitAsync(
                        command,
                        () => TransactionIdAssignment.Valid(transactionId),
                        exchangeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await session.ExecuteAsync(
                        command,
                        () => TransactionIdAssignment.Valid(transactionId),
                        exchangeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (response.Failure is { } failure)
            {
                return ClassifyFailure(failure, response.Diagnostic);
            }
            if (!setOnly && !Matches(command, response.Value))
            {
                return PoisonedIndeterminate();
            }

            return new(CoolingPadLightingSinkFailure.None);
        }
        catch (OperationCanceledException) when (!submissionPossible)
        {
            throw;
        }
        catch
        {
            return PoisonedIndeterminate();
        }
        finally
        {
            operationGate.Release();
        }
    }

    private CoolingPadLightingSinkResult ClassifyFailure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic)
    {
        bool knownNotApplied = failure is
            HidSessionFailureKind.ConflictBlocked
            or HidSessionFailureKind.ConflictDetectionFailed
            or HidSessionFailureKind.QueuedCanceled
            or HidSessionFailureKind.Exhausted
            || failure == HidSessionFailureKind.Canceled
                && diagnostic is null
                && session.Disposition == RazerCommandSessionDisposition.Healthy
            || failure == HidSessionFailureKind.ProtocolFailure
                && diagnostic?.ProtocolValidationFailure
                    == HidProtocolValidationFailure.Status;
        return knownNotApplied
            ? KnownNotApplied()
            : PoisonedIndeterminate();
    }

    private CoolingPadLightingSinkResult PoisonedIndeterminate()
    {
        poison();
        return new(CoolingPadLightingSinkFailure.Indeterminate);
    }

    private static bool Matches(
        RazerCommandSpec command,
        ReadOnlyMemory<byte> payload) =>
        payload.Length == command.ExpectedPayloadLength
        && (command.PayloadValidator?.Invoke(payload) ?? true);

    private static CoolingPadLightingSinkResult KnownNotApplied() =>
        new(CoolingPadLightingSinkFailure.KnownNotApplied);

    private static HidSessionResult<CoolingPadBrightnessReading> QueryFailure(
        HidSessionFailureKind failure) => new(default, failure);
}
