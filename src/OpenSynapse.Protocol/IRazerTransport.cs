namespace OpenSynapse.Protocol;

public interface IRazerTransport
{
    Task<ReadOnlyMemory<byte>> TransactAsync(
        RazerReport report,
        CancellationToken cancellationToken);
}

public interface IHidFeatureChannel
{
    Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken);
}

public sealed class SerializedRazerTransport(
    IHidFeatureChannel channel,
    TimeSpan timeout) : IRazerTransport, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ReadOnlyMemory<byte>> TransactAsync(
        RazerReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using CancellationTokenSource exchangeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchangeTimeout.CancelAfter(timeout);
            try
            {
                byte[] response = await channel.ExchangeAsync(report.Encode(), exchangeTimeout.Token);
                return report.ValidateResponse(response);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Razer feature exchange exceeded {timeout.TotalMilliseconds:0} ms.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
