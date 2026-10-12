using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class DisplayMonitorDeviceKeyResolverTests
{
    private const string TargetPath =
        "\\\\?\\DISPLAY#SDC4226#4&29f946e3&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void Resolve_ReturnsUniqueActiveExactInterfaceMatch()
    {
        DisplayMonitorDeviceIdentity[] monitors =
        [
            new(TargetPath, "\\Registry\\Machine\\Inactive", false),
            new(TargetPath.ToLowerInvariant(), "\\Registry\\Machine\\Active", true),
            new("different", "\\Registry\\Machine\\Other", true),
        ];

        string key = DisplayMonitorDeviceKeyResolver.Resolve(TargetPath, monitors);

        Assert.Equal("\\Registry\\Machine\\Active", key);
    }

    [Fact]
    public void Resolve_FailsClosedWhenNoActiveInterfaceMatches()
    {
        DisplayMonitorDeviceIdentity[] monitors =
        [
            new(TargetPath, "\\Registry\\Machine\\Inactive", false),
        ];

        Assert.Throws<InvalidOperationException>(() =>
            DisplayMonitorDeviceKeyResolver.Resolve(TargetPath, monitors));
    }

    [Fact]
    public void Resolve_FailsClosedWhenActiveMatchIsAmbiguous()
    {
        DisplayMonitorDeviceIdentity[] monitors =
        [
            new(TargetPath, "\\Registry\\Machine\\First", true),
            new(TargetPath, "\\Registry\\Machine\\Second", true),
        ];

        Assert.Throws<InvalidOperationException>(() =>
            DisplayMonitorDeviceKeyResolver.Resolve(TargetPath, monitors));
    }

    [Fact]
    public void Resolve_FailsClosedWhenMatchedMonitorHasNoDeviceKey()
    {
        DisplayMonitorDeviceIdentity[] monitors =
        [
            new(TargetPath, string.Empty, true),
        ];

        Assert.Throws<InvalidOperationException>(() =>
            DisplayMonitorDeviceKeyResolver.Resolve(TargetPath, monitors));
    }
}
