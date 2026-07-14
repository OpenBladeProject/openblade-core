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
    private const byte ProtocolReportId = 0;
    private const int HidFeatureReportLength = RazerReport.ReportLength + 1;

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
                byte[] request = new byte[HidFeatureReportLength];
                request[0] = ProtocolReportId;
                report.Encode().CopyTo(request, 1);

                byte[] response = await channel.ExchangeAsync(request, exchangeTimeout.Token);
                if (response.Length != HidFeatureReportLength)
                {
                    throw new RazerProtocolException(
                        $"Expected an exact {HidFeatureReportLength}-byte HID feature response.");
                }

                if (response[0] != ProtocolReportId)
                {
                    throw new RazerProtocolException(
                        $"Expected HID report ID 0x{ProtocolReportId:X2}, received 0x{response[0]:X2}.");
                }

                return report.ValidateResponse(response.AsSpan(1));
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
