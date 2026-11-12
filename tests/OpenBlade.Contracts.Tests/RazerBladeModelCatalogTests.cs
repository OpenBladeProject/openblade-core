using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class RazerBladeModelCatalogTests
{
    [Fact]
    public void Catalog_SeparatesOfficialSupportFromPhysicalValidation()
    {
        Assert.Equal(21, RazerBladeModelCatalog.All.Count);
        Assert.Equal(19, RazerBladeModelCatalog.All.Count(model => model.SupportBasis == BladeSupportBasis.SourceReviewed));
        Assert.All(RazerBladeModelCatalog.All, model => Assert.True(model.IsOfficiallySupported));
        Assert.Equal(new[] { "RZ09-0528", "RZ09-0581" }, RazerBladeModelCatalog.All
            .Where(model => model.HasPhysicalValidation)
            .Select(model => model.ModelNumber).Order(StringComparer.Ordinal));
        Assert.All(RazerBladeModelCatalog.All.Where(model => !model.HasPhysicalValidation),
            model => Assert.Equal(BladeSupportBasis.SourceReviewed, model.SupportBasis));

        var unknown = new RazerBladeModelInfo("RZ09-9999", "Unknown", 2026);
        Assert.False(unknown.IsOfficiallySupported);
        Assert.False(unknown.HasPhysicalValidation);
    }

    [Theory]
    [InlineData("RZ09-0483", true, "Supported", BladeSupportBasis.SourceReviewed, "Pending")]
    [InlineData("RZ09-0301", true, "Supported", BladeSupportBasis.SourceReviewed, "Pending")]
    [InlineData("RZ09-0528", true, "Supported", BladeSupportBasis.PhysicallyValidated, "Validated")]
    [InlineData("RZ09-0483", false, "NotEstablished", BladeSupportBasis.NotEstablished, "NotEstablished")]
    [InlineData("RZ09-9999", true, "NotEstablished", BladeSupportBasis.NotEstablished, "NotEstablished")]
    public void Diagnostics_KeepSupportAndPhysicalEvidenceSeparate(
        string modelNumber, bool detected, string status, BladeSupportBasis basis, string physicalValidation)
    {
        var diagnostics = new DiagnosticExperimentalSupport(modelNumber, "pinned source", detected, true, [])
        {
            FeatureEvidence = [new("Lighting", "SourceBackedExtrapolationWithoutRgbReadback", ["pinned source"])],
        };

        Assert.Equal(status, diagnostics.SupportStatus);
        Assert.Equal(basis, diagnostics.SupportBasis);
        Assert.Equal(physicalValidation, diagnostics.PhysicalValidation);
        Assert.True(diagnostics.WritesPaused);
        Assert.Equal("SourceBackedExtrapolationWithoutRgbReadback", Assert.Single(diagnostics.FeatureEvidence).Basis);
        Assert.Equal("NotEstablished", DiagnosticExperimentalSupport.NotDetected.SupportStatus);
    }

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
        Assert.Equal("1532", model.UsbVendorId);
        Assert.Equal("02E0", model.UsbProductId);
        Assert.Equal("Razer Blade 16 (2026)", model.DisplayName);
    }

    [Theory]
    [InlineData("RZ09-0528")]
    [InlineData("RZ09-05289EN4")]
    [InlineData("rz09-05289en4")]
    public void Find_RecognizesRz090528FamilyAndFullSku(string modelNumber)
    {
        RazerBladeModelInfo model = Assert.IsType<RazerBladeModelInfo>(
            RazerBladeModelCatalog.Find(modelNumber));

        Assert.Equal("RZ09-0528", model.ModelNumber);
        Assert.Equal("Razer Blade 16", model.ProductName);
        Assert.Equal(2025, model.ModelYear);
        Assert.Equal("1532", model.UsbVendorId);
        Assert.Equal("02C6", model.UsbProductId);
        Assert.Equal("Razer Blade 16 (2025)", model.DisplayName);
    }

    [Fact]
    public void Find_RecognizesBlade18Identity()
    {
        RazerBladeModelInfo model = Assert.IsType<RazerBladeModelInfo>(
            RazerBladeModelCatalog.Find("RZ09-0484"));
        Assert.Equal("Razer Blade 18", model.ProductName);
        Assert.Equal(2023, model.ModelYear);
        Assert.Equal("1532", model.UsbVendorId);
        Assert.Equal("02A0", model.UsbProductId);
    }

    [Theory]
    [InlineData("RZ09-0301", "Razer Blade 15 Advanced", 2019, "0245")]
    [InlineData("RZ09-0300", "Razer Blade 15 Base", 2019, "0246")]
    [InlineData("RZ09-0409", "Razer Blade 15 Advanced", 2021, "0276")]
    [InlineData("RZ09-0287", "Razer Blade Pro 17", 2019, "0234")]
    [InlineData("RZ09-0330", "Razer Blade 15 Advanced", 2020, "0253")]
    [InlineData("RZ09-0351", "Razer Blade 15 Base", 2020, "0268")]
    [InlineData("RZ09-0370", "Razer Blade 14", 2021, "0270")]
    [InlineData("RZ09-0427", "Razer Blade 14", 2022, "028C")]
    [InlineData("RZ09-0421", "Razer Blade 15", 2022, "028A")]
    [InlineData("RZ09-0423", "Razer Blade 17", 2022, "028B")]
    public void Find_RecognizesAdditionalSourceReviewedIdentities(
        string modelNumber, string productName, int year, string productId)
    {
        RazerBladeModelInfo model = Assert.IsType<RazerBladeModelInfo>(
            RazerBladeModelCatalog.Find(modelNumber));

        Assert.Equal(modelNumber, model.ModelNumber);
        Assert.Equal(productName, model.ProductName);
        Assert.Equal(year, model.ModelYear);
        Assert.Equal("1532", model.UsbVendorId);
        Assert.Equal(productId, model.UsbProductId);
        Assert.Equal(BladeSupportBasis.SourceReviewed, model.SupportBasis);
        Assert.True(model.IsOfficiallySupported);
        Assert.False(model.HasPhysicalValidation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RZ09-9999")]
    [InlineData("RZ09-0313")]
    [InlineData("RZ09-0314")]
    [InlineData("RZ09-0329")]
    public void Find_RejectsMissingOrUnsupportedModels(string? modelNumber)
    {
        Assert.Null(RazerBladeModelCatalog.Find(modelNumber));
    }
}
