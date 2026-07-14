using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class RazerTransportTests
{
    [Fact]
    public async Task Transact_ValidatesAndReturnsResponsePayload()
    {
        RazerReport request = RazerReport.Create(0x20, 0x02, 0x03, [0x10]);
        byte[] response = Frame(RazerReport.Create(0x20, 0x02, 0x03, [0xAA, 0xBB]).Encode());
        response[1] = (byte)RazerCommandStatus.Successful;
        FakeFeatureChannel channel = new(response);
        SerializedRazerTransport transport = new(channel, TimeSpan.FromSeconds(1));

        ReadOnlyMemory<byte> payload = await transport.TransactAsync(request, CancellationToken.None);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, payload.ToArray());
        Assert.NotNull(channel.Request);
        Assert.Equal(91, channel.Request.Length);
        Assert.Equal(0, channel.Request[0]);
        Assert.Equal(request.Encode(), channel.Request[1..]);
    }

    [Fact]
    public async Task Transact_RejectsResponseWithWrongReportId()
    {
        RazerReport request = RazerReport.Create(0x20, 0x02, 0x03, []);
        byte[] response = Frame(request.Encode(), reportId: 1);
        SerializedRazerTransport transport = new(new FakeFeatureChannel(response), TimeSpan.FromSeconds(1));

        RazerProtocolException exception = await Assert.ThrowsAsync<RazerProtocolException>(() =>
            transport.TransactAsync(request, CancellationToken.None));

        Assert.Contains("report ID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(92)]
    public async Task Transact_RejectsResponseThatIsNotExactly91Bytes(int responseLength)
    {
        RazerReport request = RazerReport.Create(0x20, 0x02, 0x03, []);
        byte[] response = new byte[responseLength];
        SerializedRazerTransport transport = new(new FakeFeatureChannel(response), TimeSpan.FromSeconds(1));

        RazerProtocolException exception = await Assert.ThrowsAsync<RazerProtocolException>(() =>
            transport.TransactAsync(request, CancellationToken.None));

        Assert.Contains("91", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transact_TimesOutAStalledFeatureExchange()
    {
        SerializedRazerTransport transport = new(new StalledFeatureChannel(), TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TimeoutException>(() => transport.TransactAsync(
            RazerReport.Create(1, 1, 1, []),
            CancellationToken.None));
    }

    private static byte[] Frame(byte[] envelope, byte reportId = 0) => [reportId, .. envelope];

    private sealed class FakeFeatureChannel(byte[] response) : IHidFeatureChannel
    {
        public byte[]? Request { get; private set; }

        public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response);
        }
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
