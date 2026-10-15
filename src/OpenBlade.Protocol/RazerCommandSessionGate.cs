namespace OpenBlade.Protocol;

/// <summary>
/// Serializes firmware traffic across sessions through native retirement, including a
/// submitted operation whose managed cancellation returns before its callback.
/// </summary>
internal sealed class RazerCommandSessionGate(
    IRazerCommandSession inner,
    SemaphoreSlim gate,
    bool ownsInner = false) : IRazerCommandSession, IRazerSetFeatureCommandSession
{
    public RazerCommandSessionDisposition Disposition => inner.Disposition;

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
            ReleaseAfterRetirement(gate, retirement);
        }
    }

    internal static void ReleaseAfterRetirement(SemaphoreSlim gate, Task? retirement)
    {
        if (retirement is null || retirement.IsCompletedSuccessfully)
        {
            gate.Release();
            return;
        }
        _ = ReleaseWhenRetiredAsync(gate, retirement);
    }

    private static async Task ReleaseWhenRetiredAsync(SemaphoreSlim gate, Task retirement)
    {
        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch
        {
            // An unconfirmed native lifetime must keep all other interfaces
            // blocked. Admission remains bounded; no grace period proves safety.
            return;
        }
        gate.Release();
    }

    // ValidationActor retains ownership for admitted ports; experimental ports
    // own their raw session directly and opt in to forwarding disposal.
    public ValueTask DisposeAsync() => ownsInner ? inner.DisposeAsync() : ValueTask.CompletedTask;
}
