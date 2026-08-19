using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ThermalModeProtocolTests
{
    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x02)]
    public void CreateQuery_UsesExactReadOnlyCommand(
        int channelValue,
        byte channelByte)
    {
        var channel = (ThermalChannel)channelValue;

        byte[] encoded = ThermalModeProtocol.CreateQuery(0x31, channel).Encode();

        Assert.Equal(0x31, encoded[1]);
        Assert.Equal(4, encoded[5]);
        Assert.Equal(0x0D, encoded[6]);
        Assert.Equal(0x82, encoded[7]);
        Assert.Equal([0x01, channelByte, 0x00, 0x00], encoded[8..12]);
    }

    [Theory]
    [InlineData(0x00, 0x00, 0)]
    [InlineData(0x05, 0x00, 1)]
    [InlineData(0x02, 0x00, 2)]
    [InlineData(0x00, 0x01, 3)]
    [InlineData(0x04, 0x01, 3)]
    [InlineData(0x03, 0x00, 4)]
    [InlineData(0x06, 0x00, 5)]
    [InlineData(0x04, 0x00, 6)]
    [InlineData(0x07, 0x00, 7)]
    public void TryDecodeQuery_AcceptsOnlyKnownClosedPairs(
        byte mode,
        byte fanValue,
        int expectedStateValue)
    {
        var expectedState = (ThermalModeState)expectedStateValue;
        Assert.True(ThermalModeProtocol.TryDecodeQuery(
            [0x01, 0x02, mode, fanValue],
            ThermalChannel.Second,
            out ThermalModeReading reading));
        Assert.Equal(ThermalChannel.Second, reading.Channel);
        Assert.Equal(expectedState, reading.State);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x02, 0x01, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x00, 0x00 })]
    public void TryDecodeQuery_RejectsMalformedPrefixLengthOrMismatchedId(byte[] payload)
    {
        Assert.False(ThermalModeProtocol.TryDecodeQuery(
            payload,
            ThermalChannel.First,
            out _));
    }

    [Theory]
    [InlineData(0x05, 0x01)]
    [InlineData(0x02, 0x01)]
    [InlineData(0x01, 0x00)]
    [InlineData(0x07, 0x01)]
    [InlineData(0xFF, 0xFF)]
    public void TryDecodeQuery_RejectsUnsupportedOrUnknownModeValuePairs(
        byte mode,
        byte fanValue)
    {
        Assert.False(ThermalModeProtocol.TryDecodeQuery(
            [0x01, 0x01, mode, fanValue],
            ThermalChannel.First,
            out _));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0)]
    [InlineData(0x05, 0x00, 1)]
    [InlineData(0x02, 0x00, 2)]
    [InlineData(0x00, 0x01, 3)]
    [InlineData(0x04, 0x01, 3)]
    [InlineData(0x03, 0x00, 4)]
    [InlineData(0x06, 0x00, 5)]
    [InlineData(0x04, 0x00, 6)]
    [InlineData(0x07, 0x00, 7)]
    public void TryDecodeMatchingPair_ReturnsSharedState(
        byte mode,
        byte fanValue,
        int expectedStateValue)
    {
        var expectedState = (ThermalModeState)expectedStateValue;
        Assert.True(ThermalModeProtocol.TryDecodeMatchingPair(
            [0x01, 0x01, mode, fanValue],
            [0x01, 0x02, mode, fanValue],
            out ThermalModeState state));
        Assert.Equal(expectedState, state);
    }

    [Fact]
    public void TryDecodeMatchingPair_RejectsDisagreement()
    {
        Assert.False(ThermalModeProtocol.TryDecodeMatchingPair(
            [0x01, 0x01, 0x00, 0x00],
            [0x01, 0x02, 0x02, 0x00],
            out _));
    }

    [Fact]
    public void UnknownChannel_IsRejected()
    {
        var unknown = (ThermalChannel)0x03;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ThermalModeProtocol.CreateQuery(0x31, unknown));
        Assert.False(ThermalModeProtocol.TryDecodeQuery(
            [0x01, 0x03, 0x00, 0x00],
            unknown,
            out _));
    }
}
