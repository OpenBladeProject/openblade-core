using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TestedPlatformCatalogTests
{
    [Fact]
    public void Catalog_ListsTheValidatedTargetVersions()
    {
        TestedPlatformVersionSet target = Assert.Single(TestedPlatformCatalog.Entries);

        Assert.Equal("RZ09-0581", target.ModelNumber);
        Assert.Equal(["3.01"], target.BiosVersions);
        Assert.Equal(["1.04"], target.EcVersions);
        Assert.Equal(["1.5.0.0", "1.6.0.0"], target.McuVersions);
    }

    [Theory]
    [InlineData("1.5.0.0")]
    [InlineData("1.6.0.0")]
    public void Assess_AcceptsEachTestedMcuVersion(string mcuVersion)
    {
        TestedPlatformAssessment assessment = TestedPlatformCatalog.Assess(
            "RZ09-05819EN4",
            new FirmwareVersions(" 3.01 ", "1.04", mcuVersion, "ignored"));

        Assert.True(assessment.IsEvaluated);
        Assert.True(assessment.MatchesTestedConfiguration);
        Assert.Empty(assessment.Mismatches);
    }

    [Fact]
    public void Assess_ReportsFirmwareValuesOutsideTheTestedList()
    {
        TestedPlatformAssessment assessment = TestedPlatformCatalog.Assess(
            "RZ09-0581",
            new FirmwareVersions("3.02", "1.04", "1.7.0.0", "ignored"));

        Assert.False(assessment.MatchesTestedConfiguration);
        Assert.Collection(
            assessment.Mismatches,
            mismatch =>
            {
                Assert.Equal("BIOS", mismatch.Component);
                Assert.Equal("3.02", mismatch.CurrentValue);
                Assert.Equal(["3.01"], mismatch.TestedValues);
            },
            mismatch =>
            {
                Assert.Equal("MCU", mismatch.Component);
                Assert.Equal("1.7.0.0", mismatch.CurrentValue);
                Assert.Equal(["1.5.0.0", "1.6.0.0"], mismatch.TestedValues);
            });
    }

    [Fact]
    public void Assess_ReportsAnUnknownModelAsHardwareMismatch()
    {
        TestedPlatformAssessment assessment = TestedPlatformCatalog.Assess(
            "RZ09-9999",
            new FirmwareVersions("3.01", "1.04", "1.6.0.0", "ignored"));

        TestedPlatformVersionMismatch mismatch = Assert.Single(assessment.Mismatches);
        Assert.Equal("Model", mismatch.Component);
        Assert.Equal("RZ09-9999", mismatch.CurrentValue);
        Assert.Equal(["RZ09-0581"], mismatch.TestedValues);
    }

    [Fact]
    public void Assess_UsesUnavailableForFirmwareThatCannotBeRead()
    {
        TestedPlatformAssessment assessment = TestedPlatformCatalog.Assess(
            "RZ09-0581",
            firmware: null);

        Assert.Equal(
            ["BIOS", "EC", "MCU"],
            assessment.Mismatches.Select(mismatch => mismatch.Component));
        Assert.All(
            assessment.Mismatches,
            mismatch => Assert.Equal("Unavailable", mismatch.CurrentValue));
    }
}
