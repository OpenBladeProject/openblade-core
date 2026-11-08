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
