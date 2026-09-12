using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class BrokerStateFileTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2048)]
    public async Task ReadAsync_AcceptsContentUpToTheLimit(int length)
    {
        byte[] expected = Enumerable.Repeat((byte)42, length).ToArray();
        using MemoryStream stream = new(expected);

        Assert.Equal(expected, await BrokerStateFile.ReadAsync(stream, 2048, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedLengthBeforeReading()
    {
        using ObservedStream stream = new(new byte[2049], reportedLength: 2049);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BrokerStateFile.ReadAsync(stream, 2048, CancellationToken.None));

        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_RejectsGrowthWithOnlyOneExtraByteRead()
    {
        using ObservedStream stream = new(new byte[8192], reportedLength: 2048);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BrokerStateFile.ReadAsync(stream, 2048, CancellationToken.None));

        Assert.Equal(2049, stream.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_RejectsTruncationAndObservesCancellation()
    {
        using ObservedStream truncated = new(new byte[4], reportedLength: 5);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            BrokerStateFile.ReadAsync(truncated, 2048, CancellationToken.None));

        using ObservedStream canceled = new(new byte[4], reportedLength: 4);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrokerStateFile.ReadAsync(canceled, 2048, new CancellationToken(true)));
        Assert.Equal(0, canceled.BytesRead);
    }

    private sealed class ObservedStream(byte[] bytes, long reportedLength) : MemoryStream(bytes)
    {
        public override long Length => reportedLength;
        public int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}
