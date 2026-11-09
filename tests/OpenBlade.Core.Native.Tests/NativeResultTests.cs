using OpenBlade.Core.Native;

namespace OpenBlade.Core.Native.Tests;

public sealed class NativeResultTests
{
    [Theory]
    [InlineData(0u, 16u, 0u, 0u, 0u, 91u)]
    [InlineData(0u, 16u, 0u, 1u, 0u, 90u)]
    [InlineData(0u, 16u, 0u, 0u, 1u, 90u)]
    [InlineData(0u, 15u, 0u, 0u, 0u, 90u)]
    [InlineData(0u, 16u, 1u, 0u, 0u, 90u)]
    [InlineData(0u, 16u, 0u, 0u, 0u, 89u)]
    [InlineData(6u, 16u, 6u, 0u, 0u, 0u)]
    [InlineData(1u, 16u, 1u, 1u, 0u, 0u)]
    [InlineData(2u, 16u, 2u, 7u, 0u, 0u)]
    [InlineData(3u, 16u, 3u, 0u, 90u, 90u)]
    [InlineData(3u, 16u, 3u, 0u, 91u, 0u)]
    [InlineData(4u, 16u, 4u, 0u, 0u, 0u)]
    [InlineData(4u, 16u, 4u, 8u, 0u, 0u)]
    [InlineData(4u, 16u, 4u, 2u, 0u, 1u)]
    [InlineData(5u, 16u, 5u, 0u, 1u, 0u)]
    public void InconsistentNativeResultsNeverExposeOutput(uint status, uint size, uint category,
        uint detail, uint required, uint written)
    {
        NativeErrorRecord error = new() { StructSize = size, Category = category, Detail = detail, RequiredLength = required };
        NativeCodecResult result = NativeReportCodec.Interpret(status, error, written, new byte[90], 90);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Output);
        Assert.Equal(NativeBoundaryErrorKind.InvalidNativeResult, result.Error!.Kind);
    }

    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(2u, 1u)]
    [InlineData(2u, 6u)]
    [InlineData(4u, 1u)]
    [InlineData(4u, 7u)]
    [InlineData(5u, 0u)]
    public void ValidNativeErrorsPreserveNumericTaxonomy(uint category, uint detail)
    {
        NativeErrorRecord error = new() { StructSize = 16, Category = category, Detail = detail };
        NativeCodecResult result = NativeReportCodec.Interpret(category, error, 0, new byte[90], 90);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Output);
        Assert.Equal((NativeBoundaryErrorKind)category, result.Error!.Kind);
        Assert.Equal(detail, result.Error.Detail);
    }
}
