using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CustomPerformanceProtocolTests
{
    [Theory]
    [InlineData(0x01, "010100")]
    [InlineData(0x02, "010200")]
    public void CreateQueryPayload_UsesCapturedClosedSelector(
        byte domain,
        string expectedHex)
    {
        Assert.Equal(
            expectedHex,
            Convert.ToHexString(CustomPerformanceProtocol.CreateQueryPayload(
                (CustomPerformanceDomain)domain)));
    }

    [Theory]
    [InlineData(0x00, CustomPerformanceLevel.Low)]
    [InlineData(0x01, CustomPerformanceLevel.Medium)]
    [InlineData(0x02, CustomPerformanceLevel.High)]
    public void TryDecodeQuery_MapsCapturedLevels(
        byte encoded,
        CustomPerformanceLevel expected)
    {
        Assert.True(CustomPerformanceProtocol.TryDecodeQuery(
            [0x01, 0x02, encoded],
            CustomPerformanceDomain.Gpu,
            out CustomPerformanceLevel actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { 0x01, 0x01, 0x01 })]
    [InlineData(new byte[] { 0x00, 0x02, 0x01 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    public void TryDecodeQuery_RejectsMalformedOrUnknownPayload(byte[] payload)
    {
        Assert.False(CustomPerformanceProtocol.TryDecodeQuery(
            payload,
            CustomPerformanceDomain.Gpu,
            out _));
    }
}
