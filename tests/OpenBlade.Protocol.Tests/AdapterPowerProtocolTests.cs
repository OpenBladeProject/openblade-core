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
    [InlineData("0111", AdapterPowerState.LimitedExternal)]
    [InlineData("0811", AdapterPowerState.LimitedExternal)]
    [InlineData("0C0C", AdapterPowerState.SufficientExternal)]
    [InlineData("0C11", AdapterPowerState.LimitedExternal)]
    [InlineData("110C", AdapterPowerState.FullPower280W)]
    [InlineData("1112", AdapterPowerState.LimitedExternal)]
    [InlineData("1213", AdapterPowerState.LimitedExternal)]
    [InlineData("0F11", AdapterPowerState.SufficientExternal)]
    [InlineData("110F", AdapterPowerState.LimitedExternal)]
    [InlineData("0707", AdapterPowerState.SufficientExternal)]
    [InlineData("1313", AdapterPowerState.SufficientExternal)]
    [InlineData("100C", AdapterPowerState.Unknown)]
    [InlineData("0C00", AdapterPowerState.Unknown)]
    [InlineData("0CFF", AdapterPowerState.Unknown)]
    [InlineData("1110", AdapterPowerState.Unknown)]
    [InlineData("FFFF", AdapterPowerState.Unknown)]
    public void TryDecodeQuery_UsesSynapseWattageAndRecommendation(
        string payloadHex,
        AdapterPowerState expected)
    {
        Assert.True(AdapterPowerProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out AdapterPowerState actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 40)]
    [InlineData(0x02, 45)]
    [InlineData(0x03, 60)]
    [InlineData(0x04, 65)]
    [InlineData(0x05, 80)]
    [InlineData(0x06, 90)]
    [InlineData(0x07, 100)]
    [InlineData(0x08, 120)]
    [InlineData(0x09, 130)]
    [InlineData(0x0A, 150)]
    [InlineData(0x0B, 180)]
    [InlineData(0x0C, 200)]
    [InlineData(0x0D, 230)]
    [InlineData(0x0E, 250)]
    [InlineData(0x0F, 310)]
    [InlineData(0x11, 280)]
    [InlineData(0x12, 330)]
    [InlineData(0x13, 400)]
    public void SynapseTable_MapsEveryDefinedWattage(byte code, int watts)
    {
        Assert.Equal(watts, AdapterPowerProtocol.GetWatts(code));
        if (watts > 0)
        {
            Assert.True(AdapterPowerProtocol.TryDecodeQuery([code, code], out var equal));
            Assert.True(HyperBoostValidationPower.IsAdmittedFullPower(equal));
            Assert.True(AdapterPowerProtocol.TryDecodeQuery([0x01, code], out var minimum));
            Assert.Equal(watts > 40 ? AdapterPowerState.LimitedExternal
                : AdapterPowerState.SufficientExternal, minimum);
        }
    }

    [Fact]
    public void SynapseTable_LeavesEveryUndefinedCodeUnknown()
    {
        for (int code = 0; code <= byte.MaxValue; code++)
        {
            if (code <= 0x13 && code != 0x10)
            {
                continue;
            }
            Assert.Null(AdapterPowerProtocol.GetWatts((byte)code));
            Assert.True(AdapterPowerProtocol.TryDecodeQuery([(byte)code, 0x0C], out var actual));
            Assert.Equal(AdapterPowerState.Unknown, actual);
            Assert.True(AdapterPowerProtocol.TryDecodeQuery([0x0C, (byte)code], out var recommended));
            Assert.Equal(AdapterPowerState.Unknown, recommended);
        }
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
