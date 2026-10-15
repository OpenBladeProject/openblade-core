namespace OpenBlade.Protocol;

/// <summary>
/// Serializes 0581 firmware traffic through native retirement, including a
/// submitted operation whose managed cancellation returns before its callback.
/// </summary>
internal sealed class Rz090581CommandSessionGate(
    IRazerCommandSession inner,
    SemaphoreSlim gate) : IRazerCommandSession, IRazerSetFeatureCommandSession
{
    public RazerCommandSessionDisposition Disposition => inner.Disposition;

    public void RetainQueryFailure(byte classId, byte commandId, FirmwareQueryOutcome outcome) =>
        inner.RetainQueryFailure(classId, commandId, outcome);

    public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken) => ExecuteGatedAsync(
            () => inner.ExecuteAsync(command, assignTransactionId, exchangeTimeout, cancellationToken),
            exchangeTimeout, cancellationToken);

    public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken) =>
        inner is IRazerSetFeatureCommandSession submitter
            ? ExecuteGatedAsync(
                () => submitter.SubmitAsync(command, assignTransactionId, exchangeTimeout, cancellationToken),
                exchangeTimeout, cancellationToken)
            : Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ProtocolFailure));

    public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
        IReadOnlyList<RazerCommandSpec> commands,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken) =>
        inner is IRazerSetFeatureCommandSession submitter
            ? ExecuteGatedAsync(
                () => submitter.SubmitBatchAsync(commands, assignTransactionId, exchangeTimeout, cancellationToken),
                exchangeTimeout, cancellationToken)
            : Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ProtocolFailure));

    private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteGatedAsync(
        Func<Task<HidSessionResult<ReadOnlyMemory<byte>>>> execute,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken)
    {
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        using var deadline = new CancellationTokenSource(exchangeTimeout);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await gate.WaitAsync(waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested)
        {
            return new(default, cancellationToken.IsCancellationRequested
                ? HidSessionFailureKind.QueuedCanceled
                : HidSessionFailureKind.Exhausted);
        }

        Task? retirement = null;
        try
        {
            HidSessionResult<ReadOnlyMemory<byte>> result = await execute().ConfigureAwait(false);
            retirement = result.NativeRetirement;
            return result;
        }
        catch (NativeOperationFinalizationException exception)
        {
            retirement = exception.Result.NativeRetirement;
            throw;
        }
        finally
        {
            Rz090581FeatureCommandGate.ReleaseAfterRetirement(gate, retirement);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
