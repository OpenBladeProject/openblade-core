using OpenBlade.Core.Native;

namespace OpenBlade.Core.Native.Tests;

public sealed class NativeEncodeIntoTests
{
    [Fact]
    public void SpanEncoderMatchesAllocatingEncoderWithoutHotPathAllocation()
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        byte[] payload = [1, 2, 3];
        byte[] output = new byte[90];
        for (int index = 0; index < 10; index++)
        {
            Assert.Null(codec.EncodeInto(1, 2, 3, payload, output));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        NativeBoundaryError? error = null;
        for (int index = 0; index < 100; index++)
        {
            error = codec.EncodeInto(1, 2, 3, payload, output);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(error);
        Assert.Equal(0, allocated);
        Assert.Equal(codec.Encode(1, 2, 3, payload).Output, output);
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(85)]
    public void DirectSpanEncoderProjectsOverlappingManagedRangesBeforeFfi(int offset)
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        byte[] buffer = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        byte[] untouched = buffer.ToArray();
        byte[] originalPayload = buffer.AsSpan(40 + offset, 80).ToArray();
        NativeCodecResult expected = codec.Encode(1, 2, 3, originalPayload);
        Assert.True(expected.IsSuccess);
        NativeBoundaryError? error = codec.EncodeInto(1, 2, 3,
            buffer.AsSpan(40 + offset, 80), buffer.AsSpan(40, 90));
        Assert.Null(error);
        Assert.Equal(expected.Output, buffer.AsSpan(40, 90).ToArray());
        Assert.Equal(untouched.AsSpan(0, 40).ToArray(), buffer.AsSpan(0, 40).ToArray());
        Assert.Equal(untouched.AsSpan(130).ToArray(), buffer.AsSpan(130).ToArray());
    }

    [Fact]
    public void ImmutableValidationInputsMayAliasWithoutSharingWritableFfiOutput()
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        byte[] response = codec.Encode(1, 2, 3, [4, 5, 6]).Output!;
        response[0] = 2;
        byte[] original = response.ToArray();
        NativeCodecResult result = codec.ValidateResponse(1, 2, 3,
            response.AsSpan(8, 3), response, 3);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(original, response);
        Array.Fill(response, (byte)0xA5);
        Assert.Equal(new byte[] { 4, 5, 6 }, result.Output);
    }

    [Fact]
    public void FailedSpanEncoderDoesNotPublishBytes()
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        byte[] output = Enumerable.Repeat((byte)0xA5, 90).ToArray();
        NativeBoundaryError? error = codec.EncodeInto(1, 2, 3, new byte[81], output);
        Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, error!.Kind);
        Assert.All(output, value => Assert.Equal(0xA5, value));
        codec.Dispose();
        error = codec.EncodeInto(1, 2, 3, [], output);
        Assert.Equal(NativeBoundaryErrorKind.Disposed, error!.Kind);
        Assert.All(output, value => Assert.Equal(0xA5, value));
    }
}
