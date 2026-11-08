using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class FanStopGuardianTransportTests
{
    [Fact]
    public async Task FramesRoundTripTypedOperationAndBoundLength()
    {
        var command = new FanStopGuardianCommand(Guid.NewGuid(), FanStopGuardianCommandKind.Automatic);
        using var stream = new MemoryStream();
        await FanStopGuardianTransport.WriteAsync(stream, command, default);
        Assert.Equal(stream.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()));
        stream.Position = 0;
        Assert.Equal(command, await FanStopGuardianTransport.ReadAsync<FanStopGuardianCommand>(stream, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8193)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLengthIsRejectedBeforeBodyRead(int count)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, count);
        using var stream = new MemoryStream(length);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FanStopGuardianTransport.ReadAsync<FanStopGuardianCommand>(stream, default));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"OperationId\":\"00000000-0000-0000-0000-000000000001\",\"Kind\":1,\"Kind\":2}")]
    [InlineData("{\"OperationId\":\"00000000-0000-0000-0000-000000000001\",\"Kind\":1,\"Extra\":true}")]
    public async Task MalformedMissingDuplicateAndExtraMembersAreRejected(string json)
    {
        using var stream = Frame(json);
        Exception? failure = await Record.ExceptionAsync(() =>
            FanStopGuardianTransport.ReadAsync<FanStopGuardianCommand>(stream, default));
        Assert.True(failure is JsonException or InvalidDataException);
    }

    [Fact]
    public async Task TruncatedBodyAndOversizedOutputAreRejected()
    {
        using var stream = Frame("{}");
        stream.SetLength(stream.Length - 1);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            FanStopGuardianTransport.ReadAsync<FanStopGuardianCommand>(stream, default));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FanStopGuardianTransport.WriteAsync(output, new string('x', 8193), default));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("\\\\server\\share\\owner.exe")]
    public void ProtectedPathRejectsRelativeAndNetworkPaths(string path)
    {
        Assert.Throws<IOException>(() => FanStopGuardianTransport.VerifyProtectedPath(path, false));
    }

    private static MemoryStream Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream();
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }
}
