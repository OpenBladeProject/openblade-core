namespace OpenBlade.Protocol;

/// <summary>
/// Executes typed cooling-pad acquisition, owned-target update, and release
/// operations. This type is intentionally internal and remains unreachable
/// from production composition while write admission is closed.
/// </summary>
internal sealed class CoolingPadHostControlExecutor
{
    private readonly IRazerCommandSession session;
    private readonly ValidationRunContext context;
    private readonly SemaphoreSlim operationGate;
    private readonly TimeSpan exchangeTimeout;
    private readonly Action poison;

    internal CoolingPadHostControlExecutor(
        IRazerCommandSession session,
        ValidationRunContext context,
        SemaphoreSlim operationGate,
        TimeSpan exchangeTimeout,
        Action poison)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.operationGate = operationGate
            ?? throw new ArgumentNullException(nameof(operationGate));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.poison = poison ?? throw new ArgumentNullException(nameof(poison));
    }

    internal async Task ApplyValidated2200Async(CancellationToken cancellationToken)
        => await AcquireAsync(
            new OpenBlade.Contracts.CoolingPadFanTarget(
                CoolingPadHostControlPlan.TargetRpm),
            cancellationToken).ConfigureAwait(false);

    internal async Task AcquireAsync(
        OpenBlade.Contracts.CoolingPadFanTarget target,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool firstSubmissionPossible = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryAllocateTransaction(out byte firstTransaction)
                || !context.TryAllocateTransaction(out byte secondTransaction))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The cooling-pad host-control plan could not reserve both transactions.")
                {
                    FailureKind = HidSessionFailureKind.Exhausted,
                };
            }

            CoolingPadHostControlPlan plan =
                CoolingPadHostControlPlan.CreateAcquisition(target);
            RazerCommandSpec preparation = plan.GetCommand(0);
            RazerCommandSpec demand = plan.GetCommand(1);

            cancellationToken.ThrowIfCancellationRequested();
            firstSubmissionPossible = true;
            HidSessionResult<ReadOnlyMemory<byte>> prepared =
                await session.ExecuteAsync(
                        preparation,
                        () => TransactionIdAssignment.Valid(firstTransaction),
                        exchangeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (prepared.Failure is { } preparationFailure)
            {
                BladeWriteException failure = MapFirstFailure(
                    preparationFailure,
                    prepared.Diagnostic);
                if (failure.SubmissionState == WriteSubmissionState.Indeterminate)
                {
                    poison();
                }
                throw failure;
            }
            if (!Matches(preparation, prepared.Value))
            {
                poison();
                throw Indeterminate(
                    "The cooling-pad preparation acknowledgement did not match the validated plan.");
            }

            // Preparation is now acknowledged. The demand command is part of
            // the same atomic semantic plan, so caller cancellation no longer
            // interrupts it.
            HidSessionResult<ReadOnlyMemory<byte>> applied =
                await session.ExecuteAsync(
                        demand,
                        () => TransactionIdAssignment.Valid(secondTransaction),
                        exchangeTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (applied.Failure is not null || !Matches(demand, applied.Value))
            {
                poison();
                throw Indeterminate(
                    "The cooling-pad demand outcome is indeterminate after preparation was acknowledged.");
            }
        }
        catch (BladeWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!firstSubmissionPossible)
        {
            throw;
        }
        catch (Exception exception)
        {
            poison();
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The cooling-pad host-control outcome is indeterminate.",
                exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal Task UpdateAsync(
        OpenBlade.Contracts.CoolingPadFanTarget target,
        CancellationToken cancellationToken) => ExecuteSingleAsync(
        CoolingPadHostControlPlan.CreateUpdate(target),
        "The cooling-pad target update",
        cancellationToken);

    internal Task ReleaseAsync(CancellationToken cancellationToken) =>
        ExecuteSingleAsync(
            CoolingPadHostControlPlan.CreateRelease(),
            "The cooling-pad host-control release",
            cancellationToken);

    private async Task ExecuteSingleAsync(
        RazerCommandSpec command,
        string operation,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submissionPossible = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryAllocateTransaction(out byte transaction))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    $"{operation} could not reserve a transaction.")
                {
                    FailureKind = HidSessionFailureKind.Exhausted,
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            submissionPossible = true;
            HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
                    command,
                    () => TransactionIdAssignment.Valid(transaction),
                    exchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Failure is { } failure)
            {
                BladeWriteException mapped = MapFirstFailure(
                    failure,
                    result.Diagnostic,
                    operation);
                if (mapped.SubmissionState == WriteSubmissionState.Indeterminate)
                {
                    poison();
                }
                throw mapped;
            }
            if (!Matches(command, result.Value))
            {
                poison();
                throw Indeterminate($"{operation} acknowledgement did not match.");
            }
        }
        catch (BladeWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!submissionPossible)
        {
            throw;
        }
        catch (Exception exception)
        {
            poison();
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                $"{operation} outcome is indeterminate.",
                exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private BladeWriteException MapFirstFailure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic,
        string operation = "The cooling-pad preparation")
    {
        bool knownNotApplied = failure is
            HidSessionFailureKind.ConflictBlocked
            or HidSessionFailureKind.ConflictDetectionFailed
            or HidSessionFailureKind.QueuedCanceled
            or HidSessionFailureKind.Exhausted
            || (failure == HidSessionFailureKind.Canceled
                && diagnostic is null
                && session.Disposition == RazerCommandSessionDisposition.Healthy)
            || (failure == HidSessionFailureKind.ProtocolFailure
                && diagnostic?.ProtocolValidationFailure
                    == HidProtocolValidationFailure.Status);
        return new BladeWriteException(
            knownNotApplied
                ? WriteSubmissionState.KnownNotApplied
                : WriteSubmissionState.Indeterminate,
            knownNotApplied
                ? $"{operation} was definitively not applied."
                : $"{operation} outcome is indeterminate.")
        {
            FailureKind = failure,
        };
    }

    private static bool Matches(
        RazerCommandSpec command,
        ReadOnlyMemory<byte> payload) =>
        payload.Length == command.ExpectedPayloadLength
        && (command.PayloadValidator?.Invoke(payload) ?? true);

    private static BladeWriteException Indeterminate(string message) => new(
        WriteSubmissionState.Indeterminate,
        message);
}
