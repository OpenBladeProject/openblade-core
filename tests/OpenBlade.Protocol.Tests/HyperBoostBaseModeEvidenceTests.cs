using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class HyperBoostBaseModeEvidenceTests
{
    [Fact]
    public void EmbeddedManifest_BindsExactDirectTransitionAndEvidence()
    {
        HyperBoostBaseModeAdmissionManifest manifest =
            HyperBoostBaseModeAdmissionSource.ReadValidated();

        Assert.Equal("Blade 16 - RZ09-0581", manifest.Host.Model);
        Assert.Equal("RZ09-05819EN4", manifest.Host.Sku);
        Assert.Equal("4.00", manifest.Host.BiosVersion);
        Assert.Equal(0x1532, manifest.Host.VendorId);
        Assert.Equal(0x02E0, manifest.Host.ProductId);
        Assert.Equal(7, manifest.Transition.CandidateMode);
        Assert.Equal(2, manifest.Transition.ThermalChannelCount);
        Assert.Equal("Automatic", manifest.Transition.BaselineFanMode);
        Assert.True(manifest.Transition.FullPowerAdapterRequired);
        Assert.True(manifest.Transition.ExactCoolingPadRequired);
        Assert.False(manifest.Transition.BalancedIntermediaryAllowed);
        Assert.Equal(
            "3DBE55C2E2972EE2C97FE5DC5D7A53D7F175CF37EB954E95C16E67589401010D",
            manifest.Evidence.ProductionFixtureSha256);
        Assert.Equal(
            "03FE24FC3FDC67955DC7F86D1612988FF72C1923FBA11017B369715DB41B50B9",
            manifest.Evidence.BalancedValidationAnnotationSha256);
        Assert.Equal(
            "DA96FF79C1EDED5F1F2A414CF25F8F664A93D7E54253EDB16DB4D36407E493D3",
            manifest.Evidence.BalancedRepeatAnnotationSha256);
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.True(manifest.BaseModes.Balanced.ExactApplyReadbackRestore.Validated);
        Assert.Equal(
            "captures/annotations/2026-08-09-rz09-0581-hyperboost-write-validation.json",
            manifest.BaseModes.Balanced.ExactApplyReadbackRestore.Evidence?.Artifact);
        Assert.Null(manifest.BaseModes.Silent.ExactApplyReadbackRestore.Evidence);
        Assert.Null(manifest.BaseModes.Performance.ExactApplyReadbackRestore.Evidence);
    }

    [Fact]
    public void CurrentEvidence_AdmitsBalancedAndExposesDirectBasesAsObservedOnly()
    {
        HyperBoostBaseModeCapabilities capabilities =
            HyperBoostBaseModeAdmissionSource.ReadCapabilities();

        Assert.Equal(DeviceFeatureAccess.ObservedOnly, capabilities.Silent);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, capabilities.Balanced);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, capabilities.Performance);
    }

    [Fact]
    public void ValidatedBaseModeWithoutBoundEvidence_IsRejected()
    {
        HyperBoostBaseModeAdmissionManifest manifest =
            HyperBoostBaseModeAdmissionSource.ReadValidated();
        manifest = manifest with
        {
            BaseModes = manifest.BaseModes with
            {
                Balanced = manifest.BaseModes.Balanced with
                {
                    ExactApplyReadbackRestore = new ReviewedAdmissionGate(true, null),
                },
            },
        };

        Assert.Throws<UnvalidatedCommandException>(
            () => HyperBoostBaseModeAdmissionSource.ValidateForTest(manifest));
    }
}
