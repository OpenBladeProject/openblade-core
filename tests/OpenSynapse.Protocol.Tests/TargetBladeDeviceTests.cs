using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class TargetBladeDeviceTests
{
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
}
