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

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public void ExactRecipeAdmitsCleanAndPartialStacksAndConfirmsOnlyCompleteStack(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        List<Device> clean = Tree(false, recipe);
        Assert.True(WindowsRazerDriverInstallPlatform.TryAdmitTree(clean, recipe, out Guid container));
        Assert.Equal(Container, container);
        Assert.False(IsInstalled(clean, recipe));

        List<Device> partial = Tree(true, recipe);
        partial.RemoveAt(4); // The mouse-created RzCommon child may be absent after removal.
        Assert.True(WindowsRazerDriverInstallPlatform.TryAdmitTree(partial, recipe, out _));
        Assert.False(IsInstalled(partial, recipe));
        Assert.True(IsInstalled(Tree(true, recipe), recipe));

        var other = productId == "02C6" ? RazerKeyboardDriverRecipes.Pid02E0 : RazerKeyboardDriverRecipes.Pid02C6;
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(clean, other, out _));
        Assert.False(IsInstalled(Tree(true, recipe), other));
        Assert.False(WindowsRazerDriverInstallPlatform.IsInstalled(Tree(true, recipe), Container,
            recipe.CommonHash, other.DeviceHash, recipe));
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public void UnknownOrFailedPhysicalStackIsNotARepairTarget(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        foreach (int index in new[] { 0, 1, 2, 3 })
        {
            var devices = Tree(false, recipe);
            devices[index] = devices[index] with { Service = "UnknownDriver" };
            Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
            devices = Tree(false, recipe);
            devices[index] = devices[index] with { Problem = 10 };
            Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
        }
        var filteredMouse = Tree(false, recipe);
        filteredMouse[1] = filteredMouse[1] with { Lower = ["UnreviewedFilter"] };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(filteredMouse, recipe, out _));
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public void OptionalControlChildCannotBelongToAnUnknownStack(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        var devices = Tree(true, recipe);
        devices[4] = devices[4] with { Service = "UnknownDriver" };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
        devices = Tree(true, recipe);
        devices[4] = devices[4] with { Container = Guid.NewGuid() };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
        devices = Tree(true, recipe);
        devices[1] = devices[1] with { Status = 0 };
        Assert.False(IsInstalled(devices, recipe));
        devices = Tree(true, recipe);
        devices[5] = devices[5] with { Upper = [] };
        Assert.True(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
        Assert.False(IsInstalled(devices, recipe));
        devices[5] = devices[5] with { Upper = ["UnknownVirtualFilter"] };
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
    }

    [Theory]
    [InlineData("RzCommon")]
    [InlineData("RzDev_02e0")]
    [InlineData("RzDev_02c6")]
    public void ExistingServiceCannotRedirectInstallationToAnUnknownImage(string service)
    {
        Assert.True(WindowsRazerDriverInstallPlatform.AdmitServiceBinding(false, null, null, service));
        Assert.True(WindowsRazerDriverInstallPlatform.AdmitServiceBinding(true, 1,
            @"\SystemRoot\System32\drivers\" + service + ".sys", service));
        Assert.False(WindowsRazerDriverInstallPlatform.AdmitServiceBinding(true, 2,
            @"\SystemRoot\System32\drivers\" + service + ".sys", service));
        Assert.False(WindowsRazerDriverInstallPlatform.AdmitServiceBinding(true, 1,
            @"C:\Other\" + service + ".sys", service));
        Assert.False(WindowsRazerDriverInstallPlatform.AdmitServiceBinding(true, 1,
            @"\SystemRoot\System32\drivers\another.sys", service));
        Assert.False(WindowsRazerDriverInstallPlatform.AdmitServiceBinding(true, null, null, service));
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public void SharedCommonInfCannotBindAnUnrelatedControlDevice(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        var devices = Tree(true, recipe);
        Assert.True(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
        devices.Add(new(@"RZCONTROL\VID_1532&PID_FFFF&MI_00\external", Guid.NewGuid(),
            "RzCommon", [], [], [@"USB\VID_1532&PID_FFFF&MI_02\external"], 8, 0, Pin("common"))
        {
            MatchesCommonHardware = true,
        });
        Assert.False(WindowsRazerDriverInstallPlatform.TryAdmitTree(devices, recipe, out _));
    }

    private static bool IsInstalled(List<Device> devices, RazerKeyboardDriverRecipe recipe) =>
        WindowsRazerDriverInstallPlatform.IsInstalled(devices, Container, recipe.CommonHash, recipe.DeviceHash, recipe);

    private static bool IsInstalled(List<Device> devices) => WindowsRazerDriverInstallPlatform.IsInstalled(devices, Container,
        WindowsRazerDriverInstallPlatform.CommonHash, WindowsRazerDriverInstallPlatform.DeviceHash);

    private static List<Device> Tree(bool installed) => Tree(installed, RazerKeyboardDriverRecipes.Pid02E0);

    private static List<Device> Tree(bool installed, RazerKeyboardDriverRecipe recipe)
    {
        string[] filter = installed ? [recipe.DeviceService] : [];
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
                [UsbMouse, Composite], 8, 0, Pin("common"))
            {
                MatchesCommonHardware = true,
            });
            devices.Add(new(@"RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03\test", Container, "RzDev_02e0", ["mshidkmdf"], [],
                [UsbKeyboard, Composite], 8, 0, Pin("vcon")));
        }
        return devices.Select(device => device with
        {
            Id = device.Id.Replace("02E0", recipe.ProductId, StringComparison.Ordinal),
            Ancestors = device.Ancestors.Select(id => id.Replace("02E0", recipe.ProductId, StringComparison.Ordinal)).ToArray(),
            Service = device.Service == "RzDev_02e0" ? recipe.DeviceService : device.Service,
            InfHash = device.InfHash is null ? null : recipe.Files.Single(file =>
                file.RelativePath.EndsWith(device.InfHash == Pin("common") ? "/rzcommonu.inf"
                    : device.InfHash == Pin("dkm") ? "_dkm.inf"
                    : device.InfHash == Pin("kbd") ? "_kbd.inf"
                    : device.InfHash == Pin("mou") ? "_mou.inf" : "_vcon.inf", StringComparison.OrdinalIgnoreCase)).Sha256,
        }).ToList();
    }

    private static string Pin(string part) => RazerKeyboardDriverPackage.Files.Single(file =>
        file.RelativePath.EndsWith(part == "common" ? "/rzcommonu.inf" : $"_{part}.inf", StringComparison.Ordinal)).Sha256;
}
