using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class FunctionKeyPrimaryProtocolTests
{
    [Fact]
    public void CreateQuery_UsesValidatedExactDeviceCommand()
    {
        RazerReport query = FunctionKeyPrimaryProtocol.CreateQuery(0x41);
        byte[] encoded = query.Encode();

        Assert.Equal(0x41, encoded[1]);
        Assert.Equal(2, encoded[5]);
        Assert.Equal(0x02, encoded[6]);
        Assert.Equal(0x86, encoded[7]);
        Assert.Equal([0x01, 0x00], encoded[8..10]);
    }

    [Theory]
    [InlineData("0100", FunctionKeyPrimaryMode.FunctionKeys)]
    [InlineData("0101", FunctionKeyPrimaryMode.MultimediaKeys)]
    public void TryDecodeQuery_MapsValidatedStates(
        string payloadHex,
        FunctionKeyPrimaryMode expected)
    {
        Assert.True(FunctionKeyPrimaryProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out FunctionKeyPrimaryMode actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0000")]
    [InlineData("0102")]
    [InlineData("010000")]
    public void TryDecodeQuery_RejectsUnknownOrMalformedStates(string payloadHex)
    {
        Assert.False(FunctionKeyPrimaryProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out _));
    }
}
