using OpenBlade.Capture;

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
}
