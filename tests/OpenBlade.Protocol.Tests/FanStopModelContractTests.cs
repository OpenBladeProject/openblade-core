using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class FanStopModelContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceEvidenceIsBoundToReviewedExactProfilesWithoutGrantingRuntimeWrites(bool enabled)
    {
        ushort[] reviewed = [0x029F, 0x02B7, 0x02C5, 0x028A, 0x028C, 0x0253];
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled);
            Assert.Equal(enabled && reviewed.Contains(model.ProductId), capabilities.HasFanStopSourceEvidence);
            Assert.False(capabilities.HasFanStopTrialEvidence);
            Assert.False(capabilities.SupportsFanStop);
        }
    }

    [Fact]
    public void SourceEvidenceRejectsMismatchedModelProductAndHostName()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x029F);
        foreach (ExperimentalBladeModel mismatched in new[]
        {
            model with { ModelNumber = "RZ09-0530" },
            model with { ProductId = 0x02C5 },
            model with { ProductName = "Blade 14 - RZ09-0530" },
        })
        {
            Assert.False(ExperimentalBladePort.CreateCapabilities(mismatched, true).HasFanStopSourceEvidence);
        }
    }

    [Fact]
    public void GuardianContractsMatchExistingExactHostProfilesAndKeepPhysicalStatusSeparate()
    {
        Assert.Equal(8, FanStopModelContract.All.Select(item => item.Model).Distinct().Count());
        Assert.Equal("RZ09-0581", Assert.Single(FanStopModelContract.All, item => item.PhysicallyValidated).Model);
        foreach (FanStopModelContract contract in FanStopModelContract.All.Where(item =>
                     item.Model is not ("RZ09-0581" or "RZ09-0528")))
        {
            ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ModelNumber == contract.Model);
            Assert.Equal(model.ProductId, contract.ProductId);
            Assert.Equal(model.ProductName, contract.Host.ProductName);
            Assert.Equal("Razer", contract.Host.Manufacturer);
            Assert.False(contract.PhysicallyValidated);
        }
    }
}
