using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class AdapterPowerProtocolTests
{
    [Fact]
    public void CreateQuery_UsesExactReadOnlyAdapterWattageCommand()
    {
        RazerReport query = AdapterPowerProtocol.CreateQuery(0x41);
        byte[] encoded = query.Encode();

        Assert.Equal(0x41, encoded[1]);
        Assert.Equal(2, encoded[5]);
        Assert.Equal(0x07, encoded[6]);
        Assert.Equal(0x8C, encoded[7]);
        Assert.Equal([0x00, 0x00], encoded.AsSpan(8, 2).ToArray());
    }

    [Theory]
    [InlineData("0011", AdapterPowerState.Battery)]
    [InlineData("0211", AdapterPowerState.UsbCPower45W)]
    [InlineData("0311", AdapterPowerState.UsbCPower60W)]
    [InlineData("0411", AdapterPowerState.UsbCPower65W)]
    [InlineData("0511", AdapterPowerState.UsbCPower80W)]
    [InlineData("0611", AdapterPowerState.UsbCPower90W)]
    [InlineData("0711", AdapterPowerState.UsbCPower100W)]
    [InlineData("1111", AdapterPowerState.FullPower280W)]
    [InlineData("1211", AdapterPowerState.FullPower330W)]
    [InlineData("0000", AdapterPowerState.Unknown)]
    [InlineData("0710", AdapterPowerState.Unknown)]
    [InlineData("0111", AdapterPowerState.Unknown)]
    [InlineData("0811", AdapterPowerState.Unknown)]
    [InlineData("1110", AdapterPowerState.Unknown)]
    [InlineData("FFFF", AdapterPowerState.Unknown)]
    public void TryDecodeQuery_MapsOnlyValidatedLevels(
        string payloadHex,
        AdapterPowerState expected)
    {
        Assert.True(AdapterPowerProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out AdapterPowerState actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("07")]
    [InlineData("071100")]
    public void TryDecodeQuery_RejectsMalformedPayload(string payloadHex)
    {
        Assert.False(AdapterPowerProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out AdapterPowerState actual));
        Assert.Equal(AdapterPowerState.Unknown, actual);
    }
}
