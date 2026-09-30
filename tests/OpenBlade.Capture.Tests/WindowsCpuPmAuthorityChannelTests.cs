using System.Buffers.Binary;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// Stream-only framing tests. Never instantiate the Windows pipe/process provider.
public sealed class WindowsCpuPmAuthorityChannelTests
{
    [Fact]
    public async Task InvalidRunAndAlreadyCanceledTokenStopBeforeAnyNativeAllocation()
    {
        var peer = new CpuPmAuthorityPeer(31, 100, @"C:\pinned\parent.exe", new('A', 64));
        await Assert.ThrowsAsync<ArgumentException>(() => WindowsCpuPmAuthorityChannel.ConnectChildAsync(Guid.Empty, peer, CancellationToken.None));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsCpuPmAuthorityChannel.ConnectChildAsync(Guid.NewGuid(), peer, canceled.Token));
    }
    [Fact]
    public async Task BoundedFrameRoundTripsThroughShortReads()
    {
        byte[] payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        using var output = new MemoryStream();
        await WindowsCpuPmAuthorityChannel.WriteFrameAsync(output, payload, CancellationToken.None);
        using var input = new ShortReads(output.ToArray());
        Assert.Equal(payload, await WindowsCpuPmAuthorityChannel.ReadFrameAsync(input, CancellationToken.None));
        Assert.Equal(input.Length, input.Position);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(int.MaxValue)]
    public async Task FrameLengthIsCheckedBeforeBodyAllocationOrRead(int length)
    {
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        using var input = new MemoryStream(prefix);
        await Assert.ThrowsAsync<IOException>(() => WindowsCpuPmAuthorityChannel.ReadFrameAsync(input, CancellationToken.None));
        Assert.Equal(4, input.Position);
    }
    [Fact]
    public async Task EofCancellationAndOversizedWriteRefuse()
    {
        using var shortPrefix = new MemoryStream([1, 2]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => WindowsCpuPmAuthorityChannel.ReadFrameAsync(shortPrefix, CancellationToken.None));
        using var shortBody = new MemoryStream([2, 0, 0, 0, 1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => WindowsCpuPmAuthorityChannel.ReadFrameAsync(shortBody, CancellationToken.None));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => WindowsCpuPmAuthorityChannel.WriteFrameAsync(output, new byte[4097], CancellationToken.None));
        Assert.Equal(0, output.Length);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsCpuPmAuthorityChannel.ReadFrameAsync(output, canceled.Token));
    }
    private sealed class ShortReads(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
