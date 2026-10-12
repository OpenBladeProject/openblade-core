using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadHostDemandProtocolTests
{
    [Theory]
    [InlineData(1000, 0x14)]
    [InlineData(1400, 0x1C)]
    [InlineData(1600, 0x20)]
    [InlineData(2200, 0x2C)]
    [InlineData(500, 0x0A)]
    [InlineData(3200, 0x40)]
    public void CreateSet_EncodesVendorObservedDemandFamily(int rpm, byte scaled)
    {
        var demand = new CoolingPadConfiguredRpmDemand(rpm);
        byte[] request = CoolingPadHostDemandProtocol.CreateSet(0x31, demand).Encode();
        Assert.Equal(0x31, request[1]);
        Assert.Equal(3, request[5]);
        Assert.Equal(0x0D, request[6]);
        Assert.Equal(0x01, request[7]);
        Assert.Equal([0x01, 0x05, scaled], request[8..11]);
        Assert.True(CoolingPadHostDemandProtocol.IsExactSetEcho(request[8..11], demand));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01, 0x05 })]
    [InlineData(new byte[] { 0x01, 0x05, 0x2C, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x05, 0x2C })]
    [InlineData(new byte[] { 0x01, 0x01, 0x2C })]
    [InlineData(new byte[] { 0x01, 0x05, 0x20 })]
    public void Echo_RejectsWrongGeometryFieldsOrDemand(byte[] payload) =>
        Assert.False(CoolingPadHostDemandProtocol.IsExactSetEcho(
            payload, new CoolingPadConfiguredRpmDemand(2200)));

    [Fact]
    public void DefaultDemand_CannotEncodeOrAcceptEcho()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoolingPadHostDemandProtocol.CreateSet(0x31, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoolingPadHostDemandProtocol.CreateSpec(default));
        Assert.False(CoolingPadHostDemandProtocol.IsExactSetEcho([0x01, 0x05, 0x00], default));
    }

    [Fact]
    public void CreateSpec_RequiresExactConfiguredDemandEcho()
    {
        RazerCommandSpec spec = CoolingPadHostDemandProtocol.CreateSpec(
            new CoolingPadConfiguredRpmDemand(2200));
        Assert.Equal(0x0D, spec.ClassId);
        Assert.Equal(0x01, spec.CommandId);
        Assert.Equal(new byte[] { 0x01, 0x05, 0x2C }, spec.Arguments.ToArray());
        Assert.Equal(3, spec.ExpectedPayloadLength);
        Assert.True(spec.PayloadValidator!(new byte[] { 0x01, 0x05, 0x2C }));
        Assert.False(spec.PayloadValidator!(new byte[] { 0x01, 0x05, 0x20 }));
        Assert.False(spec.PayloadValidator!(new byte[] { 0x01, 0x05, 0x2C, 0x00 }));
    }
}
