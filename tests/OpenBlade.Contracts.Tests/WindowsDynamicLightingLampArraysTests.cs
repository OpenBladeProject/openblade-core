using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class WindowsDynamicLightingLampArraysTests
{
    [Fact]
    public void CatalogListsOnlyTheReviewedLampArrayDescriptors()
    {
        // Each row was re-read from the decoded, hash-pinned MCU image.
        Assert.Equal(
            [
                ("RZ09-0528", (ushort)0x02C6, "SO6110 MCU 1.09.00", 0xDD24, false),
                ("RZ09-0529", (ushort)0x02C7, "KI8110 MCU 1.07.00", 0xEE30, true),
                ("RZ09-0509", (ushort)0x02B8, "KI8100 MCU 1.01.00", 0xEDE8, false),
                ("RZ09-0510", (ushort)0x02B7, "SO6100 MCU 1.03.00", 0x1023C, false),
                ("RZ09-0530", (ushort)0x02C5, "PI4110 MCU 1.04.00", 0xC30C, false),
            ],
            WindowsDynamicLightingLampArrays.All.Select(profile => (
                profile.ModelNumber,
                profile.ProductId,
                profile.FirmwareImage,
                profile.DescriptorOffset,
                profile.CoversVaporChamber)));
    }

    [Fact]
    public void EveryProfileMatchesItsExactCatalogModelAndProductId()
    {
        foreach (WindowsDynamicLightingLampArrayProfile profile in WindowsDynamicLightingLampArrays.All)
        {
            RazerBladeModelInfo? model = RazerBladeModelCatalog.Find(profile.ModelNumber);
            Assert.NotNull(model);
            Assert.Equal(profile.ModelNumber, model.ModelNumber);
            Assert.Equal("1532", model.UsbVendorId);
            Assert.Equal(profile.ProductId.ToString("X4"), model.UsbProductId);
            Assert.Same(profile, WindowsDynamicLightingLampArrays.Find(profile.ModelNumber));
        }
    }

    [Theory]
    [InlineData("RZ09-0483")]
    [InlineData("RZ09-0581")]
    [InlineData(null)]
    [InlineData("")]
    public void ModelsWithoutAReviewedLampArrayHaveNoProfile(string? modelNumber)
    {
        Assert.Null(WindowsDynamicLightingLampArrays.Find(modelNumber));
    }
}
