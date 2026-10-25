using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerKeyboardDriverCabinetTests
{
    private const string DeviceHash = "525613053F0155B2627E1CC83DD155D554AECD9AC84E2A473E3AD64008300E4C";
    private const string InfHash = "20F9EC60BE696C5CC87ABADFD639CE017D1DBD33C91CCCC9CBE6FD681ACF5C92";

    [Fact]
    public void PlanMapsFlatMembersToNormalizedOutputsAndAllowsPinnedDuplicates()
    {
        RazerKeyboardDriverPackage.PackageFile[] cabinet =
        [
            new("RzDev_0255.sys", 52104, DeviceHash),
            new("RzDevU_0255_Kbd.inf", 1960, InfHash),
        ];
        RazerKeyboardDriverPackage.PackageFile[] outputs =
        [
            new("rzdevu_0255_kbd/rzdevu_0255_kbd.inf", 1960, InfHash),
            new("rzdevu_0255_kbd/RzDev_0255.sys", 52104, DeviceHash),
            new("rzdevu_0255_dkm/RzDev_0255.sys", 52104, DeviceHash),
        ];

        IReadOnlyList<RazerKeyboardDriverCabinet.OutputPlan> plan =
            RazerKeyboardDriverCabinet.CreatePlan(cabinet, outputs);

        Assert.Equal(3, plan.Count);
        Assert.Equal("RzDevU_0255_Kbd.inf", plan[0].SourceName);
        Assert.Equal("RzDev_0255.sys", plan[1].SourceName);
        Assert.Equal("RzDev_0255.sys", plan[2].SourceName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../RzDev_0255.sys")]
    [InlineData("nested/RzDev_0255.sys")]
    [InlineData("nested\\RzDev_0255.sys")]
    [InlineData("RzDev_0255.sys:stream")]
    public void CabinetInventoryRequiresFlatNames(string name)
    {
        var cabinet = new[] { new RazerKeyboardDriverPackage.PackageFile(name, 52104, DeviceHash) };
        var outputs = new[] { new RazerKeyboardDriverPackage.PackageFile("driver/RzDev_0255.sys", 52104, DeviceHash) };

        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverCabinet.CreatePlan(cabinet, outputs));
    }

    [Fact]
    public void CabinetInventoryRejectsCaseInsensitiveAliases()
    {
        RazerKeyboardDriverPackage.PackageFile[] cabinet =
        [
            new("RzDev_0255.sys", 52104, DeviceHash),
            new("rzdev_0255.SYS", 52104, DeviceHash),
        ];
        var outputs = new[] { new RazerKeyboardDriverPackage.PackageFile("driver/RzDev_0255.sys", 52104, DeviceHash) };

        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverCabinet.CreatePlan(cabinet, outputs));
    }

    [Theory]
    [InlineData("../RzDev_0255.sys")]
    [InlineData("driver/../../RzDev_0255.sys")]
    [InlineData("driver\\RzDev_0255.sys")]
    [InlineData("driver/RzDev_0255.sys:stream")]
    public void OutputMappingRejectsUnsafePaths(string path)
    {
        var cabinet = new[] { new RazerKeyboardDriverPackage.PackageFile("RzDev_0255.sys", 52104, DeviceHash) };
        var outputs = new[] { new RazerKeyboardDriverPackage.PackageFile(path, 52104, DeviceHash) };

        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverCabinet.CreatePlan(cabinet, outputs));
    }

    [Theory]
    [InlineData("Other.sys", 52104, DeviceHash)]
    [InlineData("RzDev_0255.sys", 52103, DeviceHash)]
    [InlineData("RzDev_0255.sys", 52104, InfHash)]
    public void OutputMappingRequiresExactSourceNameSizeAndHash(string name, long length, string hash)
    {
        var cabinet = new[] { new RazerKeyboardDriverPackage.PackageFile("RzDev_0255.sys", 52104, DeviceHash) };
        var outputs = new[] { new RazerKeyboardDriverPackage.PackageFile("driver/" + name, length, hash) };

        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverCabinet.CreatePlan(cabinet, outputs));
    }

    [Fact]
    public void OutputMappingMustConsumeEveryReviewedCabinetMember()
    {
        RazerKeyboardDriverPackage.PackageFile[] cabinet =
        [
            new("RzDev_0255.sys", 52104, DeviceHash),
            new("RzDevU_0255_Kbd.inf", 1960, InfHash),
        ];
        var outputs = new[] { new RazerKeyboardDriverPackage.PackageFile("driver/RzDev_0255.sys", 52104, DeviceHash) };

        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverCabinet.CreatePlan(cabinet, outputs));
    }

    [Fact]
    public void OutputMappingRejectsCaseInsensitiveDestinationCollisions()
    {
        var cabinet = new[] { new RazerKeyboardDriverPackage.PackageFile("RzDev_0255.sys", 52104, DeviceHash) };
        RazerKeyboardDriverPackage.PackageFile[] outputs =
        [
            new("driver/RzDev_0255.sys", 52104, DeviceHash),
            new("DRIVER/rzdev_0255.SYS", 52104, DeviceHash),
        ];

        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverCabinet.CreatePlan(cabinet, outputs));
    }
}
