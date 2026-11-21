using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class WindowsDynamicLightingLampArraysTests
{
    [Fact]
    public void CatalogListsOnlyTheReviewedLampArrayModels()
    {
        // docs/source-reviewed-blade-models.md cites each model's descriptor evidence.
        Assert.Equal(
            [
                ("RZ09-0528", (ushort)0x02C6, false),
                ("RZ09-0529", (ushort)0x02C7, true),
                ("RZ09-0509", (ushort)0x02B8, false),
                ("RZ09-0510", (ushort)0x02B7, false),
                ("RZ09-0530", (ushort)0x02C5, false),
            ],
            WindowsDynamicLightingLampArrays.All.Select(profile => (
                profile.ModelNumber,
                profile.ProductId,
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
