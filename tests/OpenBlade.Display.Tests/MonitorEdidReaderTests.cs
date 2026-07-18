using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class MonitorEdidReaderTests
{
    [Fact]
    public void TryGetRegistrySubKey_MapsDisplayConfigMonitorPath()
    {
        const string path = @"\\?\DISPLAY#SDC4226#4&29f946e3&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        bool mapped = MonitorEdidReader.TryGetRegistrySubKey(path, out string? subKey);

        Assert.True(mapped);
        Assert.Equal(
            @"SYSTEM\CurrentControlSet\Enum\DISPLAY\SDC4226\4&29f946e3&0&UID8388688\Device Parameters",
            subKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\?\USB#VID_1532#instance")]
    [InlineData(@"\\?\DISPLAY#SDC4226#..\\OtherDevice")]
    public void TryGetRegistrySubKey_RejectsUnrelatedOrUnsafePaths(string path)
    {
        Assert.False(MonitorEdidReader.TryGetRegistrySubKey(path, out _));
    }
}
