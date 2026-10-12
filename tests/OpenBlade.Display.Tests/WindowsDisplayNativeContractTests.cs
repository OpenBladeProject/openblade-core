using System.Runtime.InteropServices;
using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class WindowsDisplayNativeContractTests
{
    [Fact]
    public void DisplayConfigStructures_MatchWin32Pack4Layout()
    {
        Assert.Equal(8, Marshal.SizeOf<DisplayConfigLuid>());
        Assert.Equal(8, Marshal.SizeOf<DisplayConfigRational>());
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigPathSourceInfo>());
        Assert.Equal(48, Marshal.SizeOf<DisplayConfigPathTargetInfo>());
        Assert.Equal(72, Marshal.SizeOf<DisplayConfigPathInfo>());
        Assert.Equal(48, Marshal.SizeOf<DisplayConfigVideoSignalInfo>());
        Assert.Equal(64, Marshal.SizeOf<DisplayConfigModeInfo>());
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigDeviceInfoHeader>());
        Assert.Equal(36, Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo2>());
        Assert.Equal(24, Marshal.SizeOf<DisplayConfigSetHdrState>());
        Assert.Equal(220, Marshal.SizeOf<DevMode>());
        Assert.Equal(840, Marshal.SizeOf<DisplayDevice>());
    }

    [Fact]
    public void ColorManagementConstants_MatchWindowsSdkIcmHeader()
    {
        Assert.Equal(0x6D6E7472u, WindowsDisplayNative.ClassMonitor);
    }

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(0x10u, true, false)]
    [InlineData(0x20u, false, true)]
    [InlineData(0x30u, true, true)]
    [InlineData(0xF0u, true, true)]
    public void AdvancedColorInfo2_DecodesHdrBits(uint value, bool supported, bool enabled)
    {
        Assert.Equal(supported, AdvancedColorBits.IsHdrSupported(value));
        Assert.Equal(enabled, AdvancedColorBits.IsHdrUserEnabled(value));
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(11u)]
    [InlineData(13u)]
    [InlineData(0x80000000u)]
    public void EmbeddedOutputTechnologies_AreTreatedAsInternal(uint technology)
    {
        Assert.True(WindowsDisplaySettingsController.IsInternalOutputTechnology(technology));
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(10u)]
    [InlineData(12u)]
    [InlineData(15u)]
    public void ExternalOutputTechnologies_AreNotTreatedAsInternal(uint technology)
    {
        Assert.False(WindowsDisplaySettingsController.IsInternalOutputTechnology(technology));
    }
}
