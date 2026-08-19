using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadLightingProtocolTests
{
    [Fact]
    public void CreateFrame_EncodesCapturedPrefixAndEighteenRgbSlots()
    {
        RgbColor[] slots = Enumerable.Range(0, CoolingPadLightingFrame.SlotCount)
            .Select(index => new RgbColor(
                checked((byte)index),
                checked((byte)(index + 0x20)),
                checked((byte)(index + 0x40))))
            .ToArray();

        byte[] encoded = CoolingPadLightingProtocol.CreateFrame(
            0x31,
            new CoolingPadLightingFrame(slots)).Encode();

        Assert.Equal(0x31, encoded[1]);
        Assert.Equal(CoolingPadLightingProtocol.FramePayloadLength, encoded[5]);
        Assert.Equal(0x0F, encoded[6]);
        Assert.Equal(0x03, encoded[7]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x11], encoded[8..13]);

        for (int slot = 0; slot < slots.Length; slot++)
        {
            int offset = 13 + (slot * 3);
            Assert.Equal(slots[slot].Red, encoded[offset]);
            Assert.Equal(slots[slot].Green, encoded[offset + 1]);
            Assert.Equal(slots[slot].Blue, encoded[offset + 2]);
        }
    }

    [Fact]
    public void CreateBrightnessQuery_UsesCapturedGetterContract()
    {
        byte[] encoded = CoolingPadLightingProtocol.CreateBrightnessQuery(0x32).Encode();

        Assert.Equal(0x32, encoded[1]);
        Assert.Equal(3, encoded[5]);
        Assert.Equal(0x0F, encoded[6]);
        Assert.Equal(0x84, encoded[7]);
        Assert.Equal([0x01, 0x05, 0x00], encoded[8..11]);
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(1, 0x02)]
    [InlineData(50, 0x7F)]
    [InlineData(100, 0xFF)]
    public void CreateBrightnessSet_UsesCapturedFloorConversion(byte percent, byte expectedRaw)
    {
        byte[] encoded = CoolingPadLightingProtocol.CreateBrightnessSet(0x33, percent).Encode();

        Assert.Equal(0x33, encoded[1]);
        Assert.Equal(3, encoded[5]);
        Assert.Equal(0x0F, encoded[6]);
        Assert.Equal(0x04, encoded[7]);
        Assert.Equal([0x01, 0x00, expectedRaw], encoded[8..11]);
    }

    [Fact]
    public void CreateBrightnessSet_RejectsPercentAboveOneHundred()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoolingPadLightingProtocol.CreateBrightnessSet(0x33, 101));
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 1)]
    [InlineData(0x7F, 50)]
    [InlineData(0xFF, 100)]
    public void TryDecodeBrightnessQuery_UsesCapturedCeilingConversion(
        byte raw,
        byte expectedPercent)
    {
        Assert.True(CoolingPadLightingProtocol.TryDecodeBrightnessQuery(
            [0x01, 0x05, raw],
            out CoolingPadBrightnessReading reading));
        Assert.Equal(raw, reading.Raw);
        Assert.Equal(expectedPercent, reading.Percent);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01, 0x05 })]
    [InlineData(new byte[] { 0x01, 0x05, 0xFF, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x05, 0xFF })]
    [InlineData(new byte[] { 0x01, 0x00, 0xFF })]
    public void TryDecodeBrightnessQuery_RejectsMalformedOrMismatchedPayload(byte[] payload)
    {
        Assert.False(CoolingPadLightingProtocol.TryDecodeBrightnessQuery(payload, out _));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(50, 0x7F)]
    [InlineData(100, 0xFF)]
    public void TryDecodeBrightnessSetAck_AcceptsExactSemanticEcho(
        byte expectedPercent,
        byte expectedRaw)
    {
        Assert.True(CoolingPadLightingProtocol.TryDecodeBrightnessSetAck(
            [0x01, 0x00, expectedRaw],
            expectedPercent));
    }

    [Theory]
    [InlineData(new byte[] { }, 50)]
    [InlineData(new byte[] { 0x01, 0x00 }, 50)]
    [InlineData(new byte[] { 0x01, 0x00, 0x7F, 0x00 }, 50)]
    [InlineData(new byte[] { 0x00, 0x00, 0x7F }, 50)]
    [InlineData(new byte[] { 0x01, 0x05, 0x7F }, 50)]
    [InlineData(new byte[] { 0x01, 0x00, 0x80 }, 50)]
    public void TryDecodeBrightnessSetAck_RejectsMalformedOrMismatchedEcho(
        byte[] payload,
        byte expectedPercent)
    {
        Assert.False(CoolingPadLightingProtocol.TryDecodeBrightnessSetAck(
            payload,
            expectedPercent));
    }

    [Fact]
    public void RawBrightnessCommand_PreservesNonRoundTrippableBaselineExactly()
    {
        RazerCommandSpec command =
            CoolingPadLightingProtocol.CreateBrightnessRawSetCommand(0x80);

        Assert.Equal([0x01, 0x00, 0x80], command.Arguments.ToArray());
        Assert.True(command.PayloadValidator?.Invoke(
            new byte[] { 0x01, 0x00, 0x80 }));
        Assert.False(command.PayloadValidator?.Invoke(
            new byte[] { 0x01, 0x00, 0x82 }));
    }
}
