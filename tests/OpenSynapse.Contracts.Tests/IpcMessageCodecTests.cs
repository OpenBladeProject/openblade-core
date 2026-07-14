using OpenSynapse.Contracts;

namespace OpenSynapse.Contracts.Tests;

public sealed class IpcMessageCodecTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsTypedMessage()
    {
        using MemoryStream stream = new();
        TestMessage expected = new(OpenSynapseIpc.ProtocolVersion, "status");

        await IpcMessageCodec.WriteAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        TestMessage actual = await IpcMessageCodec.ReadAsync<TestMessage>(
            stream,
            CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadAsync_RejectsMessageLargerThanSixtyFourKiB()
    {
        using MemoryStream stream = new();
        await stream.WriteAsync(BitConverter.GetBytes(OpenSynapseIpc.MaximumMessageBytes + 1));
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadAsync<TestMessage>(stream, CancellationToken.None));
    }

    private sealed record TestMessage(int Version, string Operation);
}
