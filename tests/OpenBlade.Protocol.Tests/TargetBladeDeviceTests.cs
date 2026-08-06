using System.Reflection;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class TargetBladeDeviceTests
{
    private static readonly HidInterfaceInfo ExactInterface = new(
        "approved",
        0x1532,
        0x02E0,
        0x0001,
        0x0002,
        91);

    [Fact]
    public void TypedTargetIdentityRetainsEvidenceBaselineAndSupportedBiosVersions()
    {
        Assert.Equal("RZ09-0581", TargetBladeDevice.Model);
        Assert.Equal("3.01", TargetBladeDevice.BiosVersion);
        Assert.Equal(["3.01", "4.00"], TargetBladeDevice.SupportedBiosVersions);
    }

    [Theory]
    [InlineData("3.01", true)]
    [InlineData("4.00", true)]
    [InlineData("3.02", false)]
    [InlineData("4.01", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedBiosVersion_RequiresAnAdmittedVersion(
        string? biosVersion,
        bool expected)
    {
        Assert.Equal(expected, TargetBladeDevice.IsSupportedBiosVersion(biosVersion));
    }

    [Theory]
    [InlineData(0x1532, 0x02E0, true)]
    [InlineData(0x1532, 0x029F, false)]
    [InlineData(0x1234, 0x02E0, false)]
    public void Matches_OnlyTheApprovedDevice(int vendorId, int productId, bool expected)
    {
        Assert.Equal(expected, TargetBladeDevice.Matches((ushort)vendorId, (ushort)productId));
    }

    [Theory]
    [InlineData(91, true)]
    [InlineData(90, false)]
    [InlineData(375, false)]
    [InlineData(0, false)]
    public void CanCarryProtocolReport_RequiresEnvelopePlusReportId(int length, bool expected)
    {
        Assert.Equal(expected, TargetBladeDevice.CanCarryProtocolReport((ushort)length));
    }

    [Fact]
    public void SelectProductionInterfaces_RequiresExactIdentityAndFeatureLength()
    {
        HidInterfaceInfo approved = new("approved", 0x1532, 0x02E0, 1, 2, 91);
        HidInterfaceInfo wrongVendor = new("vendor", 0x1234, 0x02E0, 1, 2, 91);
        HidInterfaceInfo wrongProduct = new("product", 0x1532, 0x029F, 1, 2, 91);
        HidInterfaceInfo wrongLength = new("length", 0x1532, 0x02E0, 1, 2, 90);
        HidInterfaceInfo wrongUsagePage = new("usage-page", 0x1532, 0x02E0, 0x0C, 2, 91);
        HidInterfaceInfo wrongUsage = new("usage", 0x1532, 0x02E0, 1, 1, 91);

        IReadOnlyList<HidInterfaceInfo> selected =
            WindowsHidEnumerator.SelectProductionInterfaces(
                [approved, wrongVendor, wrongProduct, wrongLength, wrongUsagePage, wrongUsage]);

        Assert.Equal([approved], selected);
    }

    [Fact]
    public void IsExactInteractiveValidationTarget_AcceptsExactTarget()
    {
        Assert.True(TargetBladeDevice.IsExactInteractiveValidationTarget(
            "RZ09-0581",
            ExactInterface));
    }

    public static TheoryData<string?, object> InexactTargets => new()
    {
        { "rz09-0581", ExactInterface },
        { null, ExactInterface },
        { "RZ09-0581", ExactInterface with { VendorId = 0x1234 } },
        { "RZ09-0581", ExactInterface with { ProductId = 0x029F } },
        { "RZ09-0581", ExactInterface with { UsagePage = 0x000C } },
        { "RZ09-0581", ExactInterface with { Usage = 0x0001 } },
        { "RZ09-0581", ExactInterface with { FeatureReportByteLength = 90 } },
    };

    [Theory]
    [MemberData(nameof(InexactTargets))]
    public void IsExactInteractiveValidationTarget_RejectsAnyInexactField(
        string? model,
        object interfaceInfo)
    {
        Assert.False(TargetBladeDevice.IsExactInteractiveValidationTarget(
            model,
            Assert.IsType<HidInterfaceInfo>(interfaceInfo)));
    }

    [Fact]
    public void IsExactInteractiveValidationTarget_RejectsNullInterfaceWithNullableContract()
    {
        Assert.False(TargetBladeDevice.IsExactInteractiveValidationTarget(
            TargetBladeDevice.Model,
            null));

        ParameterInfo interfaceParameter = typeof(TargetBladeDevice)
            .GetMethod(
                nameof(TargetBladeDevice.IsExactInteractiveValidationTarget),
                BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters()[1];
        NullabilityInfo nullability = new NullabilityInfoContext().Create(interfaceParameter);

        Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
    }
}
