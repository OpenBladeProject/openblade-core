using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class GamingModeProtocolTests
{
    [Fact]
    public void CreateQuery_UsesValidatedExactDeviceCommand()
    {
        RazerReport query = GamingModeProtocol.CreateQuery(0x41);
        byte[] encoded = query.Encode();

        Assert.Equal(0x41, encoded[1]);
        Assert.Equal(4, encoded[5]);
        Assert.Equal(0x00, encoded[6]);
        Assert.Equal(0x88, encoded[7]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], encoded[8..12]);
    }

    [Theory]
    [InlineData("00000000", false)]
    [InlineData("01000000", true)]
    public void TryDecodeQuery_MapsValidatedStates(
        string payloadHex,
        bool expected)
    {
        Assert.True(GamingModeProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out bool actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("000000")]
    [InlineData("02000000")]
    [InlineData("00010000")]
    [InlineData("0000000000")]
    public void TryDecodeQuery_RejectsUnknownOrMalformedStates(string payloadHex)
    {
        Assert.False(GamingModeProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out _));
    }
}
