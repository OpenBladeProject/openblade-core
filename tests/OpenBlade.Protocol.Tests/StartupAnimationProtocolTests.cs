using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class StartupAnimationProtocolTests
{
    [Fact]
    public void CreateQuery_UsesValidatedReadOnlyCommand()
    {
        RazerReport query = StartupAnimationProtocol.CreateQuery(0x41);
        byte[] encoded = query.Encode();

        Assert.Equal(0x41, encoded[1]);
        Assert.Equal(1, encoded[5]);
        Assert.Equal(0x0F, encoded[6]);
        Assert.Equal(0x98, encoded[7]);
        Assert.Equal(0x00, encoded[8]);
    }

    [Theory]
    [InlineData("00", true)]
    [InlineData("01", false)]
    public void TryDecodeQuery_MapsValidatedStates(
        string payloadHex,
        bool expected)
    {
        Assert.True(StartupAnimationProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out bool actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("02")]
    [InlineData("0000")]
    public void TryDecodeQuery_RejectsUnknownOrMalformedStates(string payloadHex)
    {
        Assert.False(StartupAnimationProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out bool actual));
        Assert.False(actual);
    }
}
