using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class DeviceModeProtocolTests
{
    [Fact]
    public void CreateQuery_UsesExactReadOnlyCommand()
    {
        byte[] encoded = DeviceModeProtocol.CreateQuery(0x31).Encode();

        Assert.Equal(0x31, encoded[1]);
        Assert.Equal(2, encoded[5]);
        Assert.Equal(0x00, encoded[6]);
        Assert.Equal(0x84, encoded[7]);
        Assert.Equal([0x00, 0x00], encoded[8..10]);
    }

    [Theory]
    [InlineData(0x00, 0x00)]
    [InlineData(0x03, 0x03)]
    public void TryDecodeQuery_AcceptsAdmittedModes(
        byte rawMode,
        byte expected)
    {
        Assert.True(DeviceModeProtocol.TryDecodeQuery(
            [rawMode, 0x00],
            out RazerDeviceMode actual));
        Assert.Equal(expected, (byte)actual);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x01 })]
    [InlineData(new byte[] { 0x02, 0x00 })]
    [InlineData(new byte[] { 0x04, 0x00 })]
    public void TryDecodeQuery_RejectsMalformedAndUnadmittedModes(byte[] payload)
    {
        Assert.False(DeviceModeProtocol.TryDecodeQuery(payload, out _));
    }

    [Theory]
    [InlineData(0x00, new byte[] { 0x00, 0x00 })]
    [InlineData(0x03, new byte[] { 0x03, 0x00 })]
    public void CreateSetArguments_OnlyBuildsTypedAdmittedModes(
        byte rawMode,
        byte[] expected)
    {
        var mode = (RazerDeviceMode)rawMode;
        Assert.Equal(expected, DeviceModeProtocol.CreateSetArguments(mode));
        Assert.True(DeviceModeProtocol.TryDecodeSet(expected, mode));
    }

    [Fact]
    public void CreateSetArguments_RejectsUnadmittedMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeviceModeProtocol.CreateSetArguments((RazerDeviceMode)0x02));
    }
}
