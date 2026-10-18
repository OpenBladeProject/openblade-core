using Device = OpenBlade.Tray.WindowsRazerDriverInstallPlatform.Device;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsRazerDriverInstallPlatformTests
{
    private static readonly Guid Container = new("4c7e1889-ecdd-4c12-89a2-51c42c1df891");
    private const string Composite = @"USB\VID_1532&PID_02E0\test";
    private const string UsbKeyboard = @"USB\VID_1532&PID_02E0&MI_01\test";
    private const string UsbMouse = @"USB\VID_1532&PID_02E0&MI_02\test";

    [Fact]
    public void ExactInboxTreeIsAnInstallTargetButNotAnInstalledStack()
    {
        List<Device> devices = Tree(installed: false);
        Assert.True(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, out Guid container));
        Assert.Equal(Container, container);
        Assert.False(IsInstalled(devices));
    }

    [Fact]
    public void CompleteBoundPinnedStackPassesIndependentReadback()
    {
        List<Device> devices = Tree(installed: true);
        Assert.True(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, out _));
        Assert.True(IsInstalled(devices));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DuplicatePhysicalOrCollectionIdentityIsRejected(int index)
    {
        List<Device> devices = Tree(false);
        devices.Add(devices[index]);
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, out _));
    }

    [Fact]
    public void AnotherModelsIdentityCannotBeSubstituted()
    {
        List<Device> devices = Tree(false);
        devices[0] = devices[0] with { Id = devices[0].Id.Replace("02E0", "02C6", StringComparison.Ordinal) };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, out _));
    }

    [Fact]
    public void EqualContainerDoesNotReplaceCommonCompositeAncestry()
    {
        List<Device> devices = Tree(false);
        devices[1] = devices[1] with { Ancestors = [@"USB\VID_1532&PID_02E0\another-device"] };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, out _));
    }

    [Fact]
    public void KeyboardInAnotherContainerIsRejected()
    {
        List<Device> devices = Tree(false);
        devices[2] = devices[2] with { Container = Guid.NewGuid() };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, out _));
    }

    [Fact]
    public void UnknownKeyboardFilterIsNeverOverwritten()
    {
        List<Device> devices = Tree(false);
        devices[2] = devices[2] with { Upper = ["UnreviewedFilter"] };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, out _));
    }

    [Fact]
    public void ProblemCodePreventsSuccessfulReadback()
    {
        List<Device> devices = Tree(true);
        devices[4] = devices[4] with { Problem = 10 };
        Assert.False(IsInstalled(devices));
    }

    [Fact]
    public void UnstartedDevicePreventsSuccessfulReadback()
    {
        List<Device> devices = Tree(true);
        devices[4] = devices[4] with { Status = 0 };
        Assert.False(IsInstalled(devices));
    }

    [Fact]
    public void WrongBoundInfPreventsSuccessfulReadback()
    {
        List<Device> devices = Tree(true);
        devices[2] = devices[2] with { InfHash = "different-package" };
        Assert.False(IsInstalled(devices));
    }

    [Fact]
    public void CorrectInfDoesNotReplaceDriverBinaryVerification()
    {
        Assert.False(WindowsRazerDriverInstallPlatform.IsInstalled(Tree(true), Container,
            WindowsRazerDriverInstallPlatform.CommonHash, "another-build"));
    }

    [Fact]
    public void ControlOnAnotherUsbBranchPreventsSuccessfulReadback()
    {
        List<Device> devices = Tree(true);
        devices[4] = devices[4] with { Ancestors = [UsbKeyboard, Composite] };
        Assert.False(IsInstalled(devices));
    }

    private static bool IsInstalled(List<Device> devices) => WindowsRazerDriverInstallPlatform.IsInstalled(devices, Container,
        WindowsRazerDriverInstallPlatform.CommonHash, WindowsRazerDriverInstallPlatform.DeviceHash);

    private static List<Device> Tree(bool installed)
    {
        string[] filter = installed ? ["RzDev_02e0"] : [];
        List<Device> devices =
        [
            new(UsbKeyboard, Container, "HidUsb", [], filter, [Composite], 8, 0, Pin("dkm")),
            new(UsbMouse, Container, "HidUsb", [], [], [Composite], 8, 0, null),
            new(@"HID\VID_1532&PID_02E0&MI_01&COL01\test", Container, "kbdhid", filter, [], [UsbKeyboard, Composite], 8, 0, Pin("kbd")),
            new(@"HID\VID_1532&PID_02E0&MI_02\test", Container, "mouhid", filter, [], [UsbMouse, Composite], 8, 0, Pin("mou")),
        ];
        if (installed)
        {
            devices.Add(new(@"RZCONTROL\VID_1532&PID_02E0&MI_00\test", Container, "RzCommon", [], [],
                [UsbMouse, Composite], 8, 0, Pin("common")));
            devices.Add(new(@"RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03\test", Container, "RzDev_02e0", [], [],
                [UsbKeyboard, Composite], 8, 0, Pin("vcon")));
        }
        return devices;
    }

    private static string Pin(string part) => RazerKeyboardDriverPackage.Files.Single(file =>
        file.RelativePath.EndsWith(part == "common" ? "/rzcommonu.inf" : $"_{part}.inf", StringComparison.Ordinal)).Sha256;
}
