namespace OpenBlade.Protocol;

/// <summary>
/// Serializes complete feature-report exchanges across independent PID 02C6
/// sessions. The device exposes battery, device-mode, logo, brightness, and
/// matrix commands through the same control interface, so overlapping
/// SetFeature/GetFeature exchanges can consume each other's response.
/// </summary>
internal sealed class Rz090528CommandSessionGate(
    IRazerCommandSession inner,
    SemaphoreSlim gate) :
    IRazerCommandSession,
    IRazerSetFeatureCommandSession
{
    internal static SemaphoreSlim Shared { get; } = new(1, 1);

    public RazerCommandSessionDisposition Disposition => inner.Disposition;

    public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await inner.ExecuteAsync(
                command,
                assignTransactionId,
                exchangeTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken)
    {
        if (inner is not IRazerSetFeatureCommandSession submitter)
        {
            return new(default, HidSessionFailureKind.ProtocolFailure);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await submitter.SubmitAsync(
                command,
                assignTransactionId,
                exchangeTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
