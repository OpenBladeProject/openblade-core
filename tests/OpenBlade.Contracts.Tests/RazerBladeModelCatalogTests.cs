using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class RazerBladeModelCatalogTests
{
    [Theory]
    [InlineData("RZ09-0581")]
    [InlineData("RZ09-05819EN4")]
    [InlineData("rz09-05819en4")]
    public void Find_RecognizesSupportedModelFamilyAndFullSku(string modelNumber)
    {
        RazerBladeModelInfo model = Assert.IsType<RazerBladeModelInfo>(
            RazerBladeModelCatalog.Find(modelNumber));

        Assert.Equal("RZ09-0581", model.ModelNumber);
        Assert.Equal("Razer Blade 16", model.ProductName);
        Assert.Equal(2026, model.ModelYear);
        Assert.Equal("Razer Blade 16 (2026)", model.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RZ09-0528")]
    public void Find_RejectsMissingOrUnsupportedModels(string? modelNumber)
    {
        Assert.Null(RazerBladeModelCatalog.Find(modelNumber));
    }
}
