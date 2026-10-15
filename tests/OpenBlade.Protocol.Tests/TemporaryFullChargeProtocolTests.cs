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
    [InlineData(0x00, false, 0x00)]
    [InlineData(0x04, true, 0x00)]
    [InlineData(0x08, false, 0x08)]
    [InlineData(0x0C, true, 0x08)]
    [InlineData(0x01, false, 0x01)]
    [InlineData(0x02, false, 0x02)]
    [InlineData(0x0F, true, 0x0B)]
    public void TryDecodeQuery_PowerModeControlBitmaskReadsOnlyTheFullChargeBit(
        byte payload,
        bool expectedActive,
        byte expectedOtherSettings)
    {
        Assert.True(TemporaryFullChargeProtocol.TryDecodeQuery(
            [payload],
            out bool active,
            out byte otherSettings));
        Assert.True(TemporaryFullChargeProtocol.TryDecodeQuery([payload], out bool shortActive));
        Assert.Equal(expectedActive, shortActive);
        Assert.Equal(expectedActive, active);
        Assert.Equal(expectedOtherSettings, otherSettings);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x10 })]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x08, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x04 })]
    public void TryDecodeQuery_PowerModeControlBitmaskRejectsUndefinedBitsAndLength(
        byte[] payload)
    {
        Assert.False(TemporaryFullChargeProtocol.TryDecodeQuery(payload, out _));
        Assert.False(TemporaryFullChargeProtocol.TryDecodeQuery(payload, out _, out _));
    }

    [Fact]
    public void OtherSettingsBlockMessage_ListsEverySettingThatIsOn()
    {
        Assert.Equal(
            "OpenBlade won't change temporary full charge because it would also change "
            + "other laptop settings that are on (local dimming).",
            TemporaryFullChargeProtocol.OtherSettingsBlockMessage(0x08));
        Assert.Equal(
            "OpenBlade won't change temporary full charge because it would also change "
            + "other laptop settings that are on (local dimming, CPU boost, maximum fan speed).",
            TemporaryFullChargeProtocol.OtherSettingsBlockMessage(0x0B));
    }

    [Fact]
    public void DescribeOtherSettings_NamesEachSetBitInPlainEnglish()
    {
        Assert.Equal(
            ["local dimming", "CPU boost", "maximum fan speed"],
            TemporaryFullChargeProtocol.DescribeOtherSettings(0x0B));
        Assert.Empty(TemporaryFullChargeProtocol.DescribeOtherSettings(0x00));
    }
}
