using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class LightingBrightnessProtocolTests
{
    [Fact]
    public void CreateQuery_UsesExactReadOnlyCommand()
    {
        byte[] encoded = LightingBrightnessProtocol.CreateQuery(0x31).Encode();

        Assert.Equal(0x31, encoded[1]);
        Assert.Equal(3, encoded[5]);
        Assert.Equal(0x03, encoded[6]);
        Assert.Equal(0x83, encoded[7]);
        Assert.Equal([0x01, 0x05, 0x00], encoded[8..11]);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7D)]
    [InlineData(0xFF)]
    public void TryDecodeQuery_AcceptsExactBrightnessPayload(byte expected)
    {
        Assert.True(LightingBrightnessProtocol.TryDecodeQuery(
            [0x01, 0x05, expected],
            out byte actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01, 0x05 })]
    [InlineData(new byte[] { 0x01, 0x05, 0xFF, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x05, 0xFF })]
    [InlineData(new byte[] { 0x01, 0x04, 0xFF })]
    public void TryDecodeQuery_RejectsMalformedOrMismatchedPayload(byte[] payload)
    {
        Assert.False(LightingBrightnessProtocol.TryDecodeQuery(payload, out _));
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x7D, 49)]
    [InlineData(0xFF, 100)]
    public void Reading_ReportsRoundedPercent(byte raw, int expectedPercent)
    {
        Assert.Equal(expectedPercent, new LightingBrightnessReading(raw).Percent);
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(77, 0xC5)]
    [InlineData(80, 0xCC)]
    [InlineData(100, 0xFF)]
    public void PercentToRaw_MatchesCapturedCeilingConversion(byte percent, byte expectedRaw)
    {
        Assert.Equal(expectedRaw, LightingBrightnessProtocol.PercentToRaw(percent));
    }
}
