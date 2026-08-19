using System.Security.Cryptography;
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
            "B1673380EC86030642D0E5F007E55EE12594B89C035C6A216C4503B5010026AB",
            manifest.Evidence.ImmediateAnnotationSha256);
        Assert.Equal(
            "2E4837EA0E9B81814FC551954031B5C5BF60A8E3CF593EEA9443684B62B09F33",
            manifest.Evidence.SustainedNoLoadPlanSha256);
        Assert.Equal(
            "2D1BBA887F0DC952EEABB39BC3E670A2CE3DA338EC05E46646B1E9F64D780086",
            manifest.Evidence.ReviewedLoadPlanSha256);
        Assert.Equal(
            "95E2333A19118480E6D8193933B7548587BF5C40D151C99BFE81E06D421C7537",
            manifest.Evidence.AdapterTransitionPlanSha256);
        Assert.Equal(3, manifest.SchemaVersion);
        Assert.True(manifest.Gates.ExactDeviceAndFirmwareReviewed.Validated);
        Assert.NotNull(manifest.Gates.ExactDeviceAndFirmwareReviewed.Evidence);
        Assert.True(
            manifest.Gates.ImmediateNoLoadApplyReadbackRestoreValidated.Validated);
        Assert.NotNull(
            manifest.Gates.ImmediateNoLoadApplyReadbackRestoreValidated.Evidence);
        Assert.True(manifest.Gates.SustainedNoLoadPlanReviewed.Validated);
        Assert.NotNull(manifest.Gates.SustainedNoLoadPlanReviewed.Evidence);
        Assert.True(manifest.Gates.SustainedNoLoadExecutionValidated.Validated);
        Assert.NotNull(manifest.Gates.SustainedNoLoadExecutionValidated.Evidence);
        Assert.True(manifest.Gates.PostRestorationAbsenceValidated.Validated);
        Assert.NotNull(manifest.Gates.PostRestorationAbsenceValidated.Evidence);
        Assert.True(manifest.Gates.ReviewedLoadPlanReviewed.Validated);
        Assert.NotNull(manifest.Gates.ReviewedLoadPlanReviewed.Evidence);
        Assert.True(manifest.Gates.ReviewedLoadValidated.Validated);
        Assert.NotNull(manifest.Gates.ReviewedLoadValidated.Evidence);
        Assert.True(manifest.Gates.AdapterTransitionValidated.Validated);
        Assert.NotNull(manifest.Gates.AdapterTransitionValidated.Evidence);
        Assert.False(manifest.Gates.LowPowerLifecycleValidated.Validated);
        Assert.Null(manifest.Gates.LowPowerLifecycleValidated.Evidence);
        Assert.False(manifest.Gates.CrashRecoveryValidated.Validated);
        Assert.Null(manifest.Gates.CrashRecoveryValidated.Evidence);
    }

    [Fact]
    public void ReviewedLoadPlanHash_MatchesPinnedFixtureBytes()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Rz09_0581_02e0_4_00_PadAbsentHyperBoostExternalLoadPlan.json");

        Assert.True(File.Exists(path), $"Pinned plan was not copied: {path}");
        Assert.Equal(
            ReviewedHyperBoostThermalPlan.TrustedPadAbsentExternalLoadSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public void ImmediateEvidenceHash_MatchesPinnedCommittedArtifactBytes()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Evidence",
            "2026-08-09-rz09-0581-hyperboost-pad-absent-validation.json");

        Assert.True(File.Exists(path), $"Pinned evidence was not copied: {path}");
        Assert.Equal(
            "B1673380EC86030642D0E5F007E55EE12594B89C035C6A216C4503B5010026AB",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Theory]
    [InlineData(
        "2026-08-19-rz09-0581-hyperboost-padless-immediate.json",
        "E64A2EB77C254704E3752641EB16BDA9E27BCB0C126167A21071A808F9F5C32A")]
    [InlineData(
        "2026-08-19-rz09-0581-hyperboost-padless-sustained-no-load.json",
        "40E6E6EE1F9EBAC212F72BDF883A5F1CEA065FCECE7916E95DFFE77FCE3EFA61")]
    [InlineData(
        "2026-08-19-rz09-0581-hyperboost-padless-steel-nomad.json",
        "4818729C2E46F7E3B3C308552C3A5E049A441F49B21DF7B45DD88545B81BB00C")]
    [InlineData(
        "2026-08-19-rz09-0581-hyperboost-padless-adapter-transition.json",
        "2BB2912F64E7B6DD1619821F9BD6253CE22C39ACD73AD42CEFA9D70C7DB42874")]
    public void CurrentValidationEvidenceHash_MatchesPinnedCommittedArtifactBytes(
        string fileName,
        string expectedSha256)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Evidence", fileName);

        Assert.True(File.Exists(path), $"Pinned evidence was not copied: {path}");
        Assert.Equal(
            expectedSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public void CurrentEvidence_UnlocksLifecycleValidationButNotProductionWrites()
    {
        HyperBoostWithoutCoolingPadEvidenceGates evidence =
            HyperBoostWithoutCoolingPadEvidenceGates.Current;

        Assert.True(evidence.ExactDeviceAndFirmwareReviewed);
        Assert.True(evidence.ImmediateNoLoadApplyReadbackRestoreValidated);
        Assert.True(evidence.SustainedNoLoadPlanReviewed);
        Assert.True(evidence.SustainedNoLoadExecutionValidated);
        Assert.True(evidence.PostRestorationAbsenceValidated);
        Assert.True(evidence.ReviewedLoadPlanReviewed);
        Assert.True(evidence.ReviewedLoadValidated);
        Assert.True(evidence.AdapterTransitionValidated);
        Assert.False(evidence.LowPowerLifecycleValidated);
        Assert.False(evidence.CrashRecoveryValidated);
        Assert.True(evidence.LifecycleValidationReady);
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
