namespace OpenBlade.Protocol.Tests;

public sealed class MicrophoneMuteIndicatorReportTests
{
    [Theory]
    [InlineData(
        0x15,
        true,
        "001500000003180400020100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001C00")]
    [InlineData(
        0x12,
        false,
        "001200000003180400020000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001D00")]
    public void CapturedSetter_EncodesExactReport(
        byte transactionId,
        bool muted,
        string expectedHex)
    {
        RazerReport report = RazerReport.Create(
            transactionId,
            0x18,
            0x04,
            [0x00, 0x02, muted ? (byte)0x01 : (byte)0x00]);

        Assert.Equal(Convert.FromHexString(expectedHex), report.Encode());
    }
}
