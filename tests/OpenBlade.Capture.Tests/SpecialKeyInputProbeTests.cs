using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Capture.Tests;

public sealed class SpecialKeyInputProbeTests
{
    [Theory]
    [InlineData("02c6", "02C6")]
    [InlineData("02E0", "02E0")]
    [InlineData("ffff", "FFFF")]
    public void NormalizeProductId_AcceptsExactlyFourHexDigits(
        string value,
        string expected)
    {
        Assert.Equal(expected, SpecialKeyInputProbe.NormalizeProductId(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2C6")]
    [InlineData("002C6")]
    [InlineData("02G6")]
    public void NormalizeProductId_RejectsInvalidValues(string value)
    {
        Assert.Throws<ArgumentException>(
            () => SpecialKeyInputProbe.NormalizeProductId(value));
    }

    [Theory]
    [InlineData(
        @"\\?\HID#VID_1532&PID_02C6&MI_00#REDACTED",
        "02C6",
        true)]
    [InlineData(
        @"\\?\hid#vid_1532&pid_02c6&mi_00#redacted",
        "02c6",
        true)]
    [InlineData(
        @"\\?\HID#VID_1532&PID_02E0&MI_00#REDACTED",
        "02C6",
        false)]
    [InlineData(
        @"\\?\HID#VID_1532&PID_02C60&MI_00#REDACTED",
        "02C6",
        false)]
    [InlineData(
        @"\\?\HID#VID_0000&PID_02C6&MI_00#REDACTED",
        "02C6",
        false)]
    public void MatchesTargetDevicePath_RequiresExactVendorAndProduct(
        string devicePath,
        string productIdHex,
        bool expected)
    {
        Assert.Equal(
            expected,
            SpecialKeyInputProbe.MatchesTargetDevicePath(devicePath, productIdHex));
    }

    [Theory]
    [InlineData("040A0000", "01004B00", SpecialKeySlot.M1)]
    [InlineData("040A0000", "01004E00", SpecialKeySlot.M2)]
    [InlineData("040A0300", null, SpecialKeySlot.M3)]
    [InlineData("040AD300", null, SpecialKeySlot.M4)]
    [InlineData("040AD400", null, SpecialKeySlot.M5)]
    public void Rz090528SemanticDecoder_UsesExactSpecialKeyMappings(
        string firstReportHex,
        string? secondReportHex,
        SpecialKeySlot expected)
    {
        var decoder = new Rz090528SpecialKeySemanticDecoder();

        Rz090528DecodedSpecialKeyInput first =
            decoder.Process(Convert.FromHexString(firstReportHex));
        Rz090528DecodedSpecialKeyInput result = secondReportHex is null
            ? first
            : decoder.Process(Convert.FromHexString(secondReportHex));

        Assert.Equal(expected, result.SpecialKey);
        Assert.Null(result.PerformanceShortcut);
    }

    [Fact]
    public void Rz090528SemanticDecoder_DecodesFunctionPWithoutM4Collision()
    {
        var decoder = new Rz090528SpecialKeySemanticDecoder();

        Assert.Equal(
            SpecialKeySlot.M4,
            decoder.Process(Convert.FromHexString("040AD300")).SpecialKey);
        Assert.Null(
            decoder.Process(Convert.FromHexString("040AD300"))
                .PerformanceShortcut);
        _ = decoder.Process(Convert.FromHexString("040A0000"));
        Rz090528DecodedSpecialKeyInput functionP =
            decoder.Process(Convert.FromHexString("01001300"));

        Assert.Null(functionP.SpecialKey);
        Assert.Equal(
            PerformanceShortcutSource.FunctionP,
            functionP.PerformanceShortcut);
    }
}
