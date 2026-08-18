using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class HyperBoostWithoutCoolingPadEvidenceTests
{
    [Fact]
    public void EmbeddedManifest_BindsExactHostTransitionAndReviewedEvidence()
    {
        HyperBoostWithoutCoolingPadAdmissionManifest manifest =
            HyperBoostWithoutCoolingPadAdmissionSource.ReadValidated();

        Assert.Equal("Blade 16 - RZ09-0581", manifest.Host.Model);
        Assert.Equal("RZ09-05819EN4", manifest.Host.Sku);
        Assert.Equal("4.00", manifest.Host.BiosVersion);
        Assert.Equal(0x1532, manifest.Host.VendorId);
        Assert.Equal(0x02E0, manifest.Host.ProductId);
        Assert.Equal("Balanced", manifest.Transition.BaselineMode);
        Assert.Equal("Automatic", manifest.Transition.BaselineFanMode);
        Assert.Equal(7, manifest.Transition.CandidateMode);
        Assert.Equal(2, manifest.Transition.ThermalChannelCount);
        Assert.True(manifest.Transition.FullPowerAdapterRequired);
        Assert.True(manifest.Transition.ExactPadAbsenceRequired);
        Assert.Equal(
            ["FullPower280W", "FullPower330W"],
            manifest.Transition.AdmittedFullPowerAdapterStates);
        Assert.True(manifest.Transition.StrictControllerExclusionRequired);
        Assert.True(manifest.Transition.PairedReadbackRequired);
        Assert.True(manifest.Transition.ExactBalancedRestoreRequired);
        Assert.True(manifest.Transition.IndeterminateRetryForbidden);
        Assert.True(manifest.Transition.PhysicalPadVidPidPresenceVetoRequired);
        Assert.Equal(
            "2798260D75AF394A650DA9E3B09D1047E006FB344A5A1DB0529AFEDA260F34EB",
            manifest.Evidence.ImmediateAnnotationSha256);
        Assert.Equal(
            "589A94BCE7050BA43195BBFE5FAC4E8EDF337D481705047C668460ACAB675D2B",
            manifest.Evidence.SustainedNoLoadPlanSha256);
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.True(manifest.Gates.ExactDeviceAndFirmwareReviewed.Validated);
        Assert.NotNull(manifest.Gates.ExactDeviceAndFirmwareReviewed.Evidence);
        Assert.True(manifest.Gates.SustainedNoLoadPlanReviewed.Validated);
        Assert.NotNull(manifest.Gates.SustainedNoLoadPlanReviewed.Evidence);
        Assert.False(manifest.Gates.CrashRecoveryValidated.Validated);
        Assert.Null(manifest.Gates.CrashRecoveryValidated.Evidence);
    }

    [Fact]
    public void CurrentEvidence_RecordsHistoricalResultWithoutAdmittingCurrentHead()
    {
        HyperBoostWithoutCoolingPadEvidenceGates evidence =
            HyperBoostWithoutCoolingPadEvidenceGates.Current;

        Assert.True(evidence.ExactDeviceAndFirmwareReviewed);
        Assert.False(evidence.ImmediateNoLoadApplyReadbackRestoreValidated);
        Assert.True(evidence.SustainedNoLoadPlanReviewed);
        Assert.False(evidence.SustainedNoLoadExecutionValidated);
        Assert.False(evidence.PostRestorationAbsenceValidated);
        Assert.False(evidence.ReviewedLoadValidated);
        Assert.False(evidence.AdapterTransitionValidated);
        Assert.False(evidence.LowPowerLifecycleValidated);
        Assert.False(evidence.CrashRecoveryValidated);
        Assert.False(evidence.WriteAdmitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UniformEvidence_RequiresEveryGate(bool admitted)
    {
        Assert.Equal(
            admitted,
            HyperBoostWithoutCoolingPadEvidenceGates
                .UniformForTest(admitted)
                .WriteAdmitted);
    }

    [Fact]
    public void ValidatedPadlessGateWithoutBoundEvidence_IsRejected()
    {
        HyperBoostWithoutCoolingPadAdmissionManifest manifest =
            HyperBoostWithoutCoolingPadAdmissionSource.ReadValidated();
        manifest = manifest with
        {
            Gates = manifest.Gates with
            {
                CrashRecoveryValidated = new ReviewedAdmissionGate(true, null),
            },
        };

        Assert.Throws<UnvalidatedCommandException>(
            () => HyperBoostWithoutCoolingPadAdmissionSource.ValidateForTest(manifest));
    }
}
