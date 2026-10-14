using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class TemporaryFullChargeProtocolTests
{
    [Fact]
    public void CreateQuery_UsesExactTypedReadCommand()
    {
        RazerReport report = TemporaryFullChargeProtocol.CreateQuery(0x31);

        byte[] encoded = report.Encode();
        Assert.Equal(0x31, encoded[1]);
        Assert.Equal(1, encoded[5]);
        Assert.Equal(0x07, encoded[6]);
        Assert.Equal(0x8F, encoded[7]);
        Assert.Equal(0x00, encoded[8]);
    }

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0x04)]
    public void CreateSet_UsesExactClosedWriteCommand(bool enabled, byte payload)
    {
        byte[] encoded = TemporaryFullChargeProtocol.CreateSet(0x32, enabled).Encode();

        Assert.Equal(0x32, encoded[1]);
        Assert.Equal(1, encoded[5]);
        Assert.Equal(0x07, encoded[6]);
        Assert.Equal(0x0F, encoded[7]);
        Assert.Equal(payload, encoded[8]);
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x04, true)]
    public void TryDecodeQuery_AcceptsOnlyClosedStatusBytes(byte payload, bool expected)
    {
        Assert.True(TemporaryFullChargeProtocol.TryDecodeQuery([payload], out bool active));
        Assert.Equal(expected, active);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x00, 0x04 })]
    public void TryDecodeQuery_RejectsEverythingOutsideClosedStatusSet(byte[] payload)
    {
        Assert.False(TemporaryFullChargeProtocol.TryDecodeQuery(payload, out _));
    }

    [Fact]
    public void TryDecodeQuery_CompletedMarkerIsInactiveOnlyWhenModelScopedAllowanceIsSet()
    {
        Assert.False(TemporaryFullChargeProtocol.TryDecodeQuery([0x08], out _));
        Assert.True(TemporaryFullChargeProtocol.TryDecodeQuery(
            [0x08],
            out bool active,
            acceptCompletedMarker: true));
        Assert.False(active);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x0C })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x08, 0x00 })]
    public void TryDecodeQuery_ModelScopedAllowanceAdmitsNothingBeyondExactCompletedMarker(
        byte[] payload)
    {
        Assert.False(TemporaryFullChargeProtocol.TryDecodeQuery(
            payload,
            out _,
            acceptCompletedMarker: true));
    }
}
