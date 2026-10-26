using OpenBlade.Contracts;
using OpenBlade.Platform;
using Device = OpenBlade.Tray.WindowsRazerDriverInstallPlatform.Device;

namespace OpenBlade.Tray.Tests;

public sealed class RazerDriverInstallationInspectionTests
{
    private static readonly Guid Container = new("8d2df00d-690e-4e8f-9056-a91c1a0e991e");
    private static RazerKeyboardDriverRecipe Recipe => RazerKeyboardDriverRecipes.Pid02C6;

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public void CompatibleUpdatedPackagesAreVerifiedWithoutBuildHashAllowlist(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(recipe => recipe.ProductId == productId);
        List<Device> devices = Roles(recipe);
        List<(string Id, string Service, string Hardware)> calls = [];
        RazerDriverApiPackageEvidence Capture(string id, string service, string hardware)
        {
            calls.Add((id, service, hardware));
            return new(true, true, "compatible-new-runtime-identity");
        }
        Assert.True(WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages(devices, recipe,
            "updated-common-content", "updated-device-content", out var failure, Capture));
        Assert.Null(failure);
        Assert.Equal(5, calls.Count);
        Assert.Equal(WindowsRazerDriverInstallPlatform.ReviewedWindowsUpdateHardwareIds(recipe), calls.Select(call => call.Hardware));
        calls.Clear();
        Assert.True(WindowsRazerDriverInstallPlatform.VerifyInstalledWindowsUpdatePackages(devices, recipe, out failure, Capture));
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public void PartialBoundUntrustedPackageCannotUseWindowsUpdateAsTrustBypass()
    {
        List<Device> devices = Roles(Recipe);
        devices.RemoveAt(4);
        var original = new DiagnosticFailure("DriverPackage", Recipe.DeviceService, "CatalogTrust", "UntrustedCatalog", HResult: -1);
        Assert.False(WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages(devices, Recipe,
            Recipe.CommonHash, Recipe.DeviceHash, out var failure,
            (_, _, _) => new(true, false, null) { FailureDetail = original }));
        Assert.Equal("ExistingBoundPackageUntrusted", failure?.Reason);
        Assert.Same(original, failure?.Cause);
    }

    [Fact]
    public void AbsentRoleDoesNotInheritTrustForUnknownOrphanedImage()
    {
        Assert.False(WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages([], Recipe,
            "unrecognized-orphan", null, out var failure, (_, _, _) => throw new InvalidOperationException()));
        Assert.Equal("OrphanedDriverImageUntrusted", failure?.Reason);
        Assert.True(WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages([], Recipe,
            Recipe.CommonHash, Recipe.DeviceHash, out failure, (_, _, _) => throw new InvalidOperationException()));
        Assert.Null(failure);
    }

    [Fact]
    public void MissingBoundImageIsRepairableButCatalogFailureIsNot()
    {
        List<Device> devices = [Roles(Recipe)[1]];
        var missing = new DiagnosticFailure("DriverPackage", Recipe.DeviceService, "ServiceImage", "ServiceImageNotFound");
        Assert.True(WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages(devices, Recipe,
            null, null, out _, (_, _, _) => new(false, false, null) { FailureDetail = missing }));
        var rejected = missing with { Stage = "CatalogTrust", Reason = "UntrustedCatalog" };
        Assert.False(WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages(devices, Recipe,
            null, null, out _, (_, _, _) => new(false, false, null) { FailureDetail = rejected }));
    }

    [Theory]
    [InlineData("{\"PublishedInfName\":\"keyboard.inf\"}", true)]
    [InlineData("{\"PublishedInfName\":\"unknown.inf\"}", false)]
    [InlineData("{\"PublishedInfName\":\"unknown.inf\",\"PublishedInfName\":\"keyboard.inf\"}", false)]
    [InlineData("[]", false)]
    [InlineData("bad", false)]
    public void KnownInboxRepairRequiresPinnedSourceAndUnambiguousEvidence(string context, bool expected)
    {
        List<Device> devices = [Roles(Recipe)[1]];
        var evidence = new RazerDriverApiPackageEvidence(true, false, null)
        {
            FailureDetail = new("DriverPackage", Recipe.DeviceService, "InfContract", "ProviderMismatch") { Context = context },
        };
        Assert.Equal(expected, WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages(devices, Recipe,
            null, Recipe.DeviceHash, out _, (_, _, _) => evidence));
        Assert.False(WindowsRazerDriverInstallPlatform.VerifyExistingWindowsUpdatePackages(devices, Recipe,
            null, "unknown-image", out _, (_, _, _) => evidence));
    }

    [Fact]
    public void ForeignInstanceWithReviewedCompatibleIdIsEnumeratedAndRefused()
    {
        string id = WindowsRazerDriverInstallPlatform.ReviewedWindowsUpdateHardwareIds(Recipe)[1];
        Assert.True(WindowsRazerDriverInstallPlatform.ShouldInspectDevice(@"ROOT\Foreign\unique", [id], Recipe));
        Assert.False(WindowsRazerDriverInstallPlatform.WindowsUpdateTargetsAdmitted(
            [new(@"ROOT\Foreign\unique", Container, "kbdhid", [], [], [], 8, 0, null) { HardwareIds = [id] }], Recipe, Container));
        Assert.False(WindowsRazerDriverInstallPlatform.ShouldInspectDevice(@"ROOT\Other\unique", ["unrelated"], Recipe));
    }

    [Fact]
    public void MatchingInstancePrefixDoesNotReplaceSameContainerAndAncestryAdmission()
    {
        var devices = Roles(Recipe);
        Assert.True(WindowsRazerDriverInstallPlatform.WindowsUpdateTargetsAdmitted(devices, Recipe, Container));
        devices[1] = devices[1] with { Container = Guid.NewGuid() };
        Assert.False(WindowsRazerDriverInstallPlatform.WindowsUpdateTargetsAdmitted(devices, Recipe, Container));
        devices = Roles(Recipe);
        devices[4] = devices[4] with { Ancestors = [@"USB\VID_1532&PID_02C6&MI_01\other"] };
        Assert.False(WindowsRazerDriverInstallPlatform.WindowsUpdateTargetsAdmitted(devices, Recipe, Container));
    }

    [Fact]
    public void BlockedReasonRetainsObservedStateWithoutUniqueDeviceSuffix()
    {
        var devices = Roles(Recipe);
        var failure = WindowsRazerDriverInstallPlatform.InspectionFailure("Topology", "PhysicalDeviceNotReady", Recipe,
            [devices[1] with { Id = devices[1].Id.Replace("test", "private-serial-value", StringComparison.Ordinal), Problem = 10 }]);
        Assert.True(failure.IsValid());
        Assert.Contains("PhysicalDeviceNotReady", failure.Reason);
        Assert.Contains("\"Problem\":10", failure.Context);
        Assert.Contains(Recipe.ProductId, failure.Context);
        Assert.DoesNotContain("private-serial-value", failure.Context);
    }

    [Fact]
    public void DependencyProgressRequiresPreservedTargetsAndNewReviewedChild()
    {
        var ids = WindowsRazerDriverInstallPlatform.ReviewedWindowsUpdateHardwareIds(Recipe);
        RazerKeyboardWindowsUpdateTarget root = new("root", [ids[0]]), child = new("child", [ids[4]]);
        Assert.True(RazerKeyboardDriverInstaller.HasNewWindowsUpdateChild([root], [root, child], Recipe));
        Assert.False(RazerKeyboardDriverInstaller.HasNewWindowsUpdateChild([root, child], [root], Recipe));
        Assert.False(RazerKeyboardDriverInstaller.HasNewWindowsUpdateChild([root], [child], Recipe));
        Assert.False(RazerKeyboardDriverInstaller.HasNewWindowsUpdateChild([root], [root with { HardwareIds = ["changed"] }, child], Recipe));
        Assert.False(RazerKeyboardDriverInstaller.HasNewWindowsUpdateChild([root], [root, new("new-root", [ids[0]])], Recipe));
    }

    [Fact]
    public void CompleteLookingInboxBindingIsRepairableRatherThanInstalledOrBlocked()
    {
        RazerDriverApiPackageEvidence Capture(string id, string service, string hardware) =>
            hardware.Contains("COL01", StringComparison.Ordinal)
                ? new(true, false, null)
                {
                    FailureDetail = new("DriverPackage", service, "InfContract", "ProviderMismatch")
                    { Context = "{\"PublishedInfName\":\"keyboard.inf\"}" },
                }
                : new(true, true, "trusted-installed-identity");
        var inspection = WindowsRazerDriverInstallPlatform.InspectWindowsUpdatePackages(Roles(Recipe), Recipe,
            Recipe.CommonHash, Recipe.DeviceHash, structurallyInstalled: true, Capture);
        Assert.True(inspection.Admitted);
        Assert.False(inspection.Installed);
        Assert.Null(inspection.FailureDetail);
    }

    [Fact]
    public void CompleteLookingMissingImageRemainsRepairableButUntrustedCatalogDoesNot()
    {
        RazerDriverApiPackageEvidence Capture(string id, string service, string hardware) => new(false, false, null)
        { FailureDetail = new("DriverPackage", service, "ServiceImage", "ServiceImageNotFound") };
        var inspection = WindowsRazerDriverInstallPlatform.InspectWindowsUpdatePackages(Roles(Recipe), Recipe,
            null, null, structurallyInstalled: true, Capture);
        Assert.True(inspection.Admitted);
        Assert.False(inspection.Installed);
        inspection = WindowsRazerDriverInstallPlatform.InspectWindowsUpdatePackages(Roles(Recipe), Recipe,
            Recipe.CommonHash, Recipe.DeviceHash, structurallyInstalled: true, (id, service, hardware) => new(true, false, null)
            { FailureDetail = new("DriverPackage", service, "CatalogTrust", "UntrustedCatalog") });
        Assert.False(inspection.Admitted);
        Assert.False(inspection.Installed);
        Assert.Equal("ExistingBoundPackageUntrusted", inspection.FailureDetail?.Reason);
    }

    private static List<Device> Roles(RazerKeyboardDriverRecipe recipe)
    {
        string[] ids = WindowsRazerDriverInstallPlatform.ReviewedWindowsUpdateHardwareIds(recipe).ToArray();
        return ids.Select((hardware, index) => new Device(
            (index == 4 ? $@"RZCONTROL\VID_1532&PID_{recipe.ProductId}&MI_00" : hardware) + @"\test",
            Container, index == 4 ? "RzCommon" : index == 3 ? recipe.DeviceService : index == 0 ? "HidUsb" : index == 1 ? "kbdhid" : "mouhid",
            index == 3 ? ["mshidkmdf"] : index is 1 or 2 ? [recipe.DeviceService] : [], index == 0 ? [recipe.DeviceService] : [],
            index == 0 ? [] : [$@"USB\VID_1532&PID_{recipe.ProductId}&MI_{(index is 2 or 4 ? "02" : "01")}\ancestor"],
            8, 0, "new-inf-content")
        { HardwareIds = [hardware] }).ToList();
    }
}
