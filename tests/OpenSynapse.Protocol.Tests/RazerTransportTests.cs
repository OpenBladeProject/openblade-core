using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class RazerTransportTests
{
    [Fact]
    public async Task Transact_ValidatesAndReturnsResponsePayload()
    {
        RazerReport request = RazerReport.Create(0x20, 0x02, 0x03, [0x10]);
        byte[] response = RazerReport.Create(0x20, 0x02, 0x03, [0xAA, 0xBB]).Encode();
        response[0] = (byte)RazerCommandStatus.Successful;
        SerializedRazerTransport transport = new(new FakeFeatureChannel(response), TimeSpan.FromSeconds(1));

        ReadOnlyMemory<byte> payload = await transport.TransactAsync(request, CancellationToken.None);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, payload.ToArray());
    }

    [Fact]
    public async Task Transact_TimesOutAStalledFeatureExchange()
    {
        SerializedRazerTransport transport = new(new StalledFeatureChannel(), TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TimeoutException>(() => transport.TransactAsync(
            RazerReport.Create(1, 1, 1, []),
            CancellationToken.None));
    }

    private sealed class FakeFeatureChannel(byte[] response) : IHidFeatureChannel
    {
        public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class StalledFeatureChannel : IHidFeatureChannel
    {
        public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }
}
