using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class PadlessHyperBoostLifecycleValidationBuildTests
{
    [Fact]
    public void NormalServiceBuildIsNotValidationMarked()
    {
        PadlessHyperBoostLifecycleValidationBuildIdentity identity =
            PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent();

        Assert.False(identity.Enabled);
        Assert.Null(identity.SourceRevision);
        Assert.Equal(64, identity.ServiceAssemblySha256.Length);
        Assert.Equal(
            PadlessHyperBoostLifecycleTarget.ForCurrentWindowsHost()?.ManifestSha256 ?? string.Empty,
            identity.AdmissionManifestSha256);
        Assert.False(identity.Matches(
            new string('A', 40),
            identity.ServiceAssemblySha256,
            identity.AdmissionManifestSha256));
    }

    [Fact]
    public void UnknownHostCannotMatchAnEmptyManifest()
    {
        var identity = new PadlessHyperBoostLifecycleValidationBuildIdentity(
            true, new string('A', 40), new string('B', 64), string.Empty);

        Assert.False(identity.Matches(new string('A', 40), new string('B', 64), string.Empty));
        Assert.Empty(PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent(null).AdmissionManifestSha256);
    }

    [Fact]
    public void ValidationBuildBindsSelectedModelManifest()
    {
        var target = new PadlessHyperBoostLifecycleTarget(
            "RZ09-0528", PerformancePreset.Performance, new string('C', 64),
            HyperBoostWithoutCoolingPadEvidenceGates.UniformForTest(false));
        var identity = new PadlessHyperBoostLifecycleValidationBuildIdentity(
            true, new string('A', 40), new string('B', 64), target.ManifestSha256);

        Assert.Equal(target.ManifestSha256,
            PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent(target).AdmissionManifestSha256);
        Assert.True(identity.Matches(new string('A', 40), new string('B', 64), target.ManifestSha256));
        Assert.False(identity.Matches(new string('A', 40), new string('B', 64), new string('D', 64)));
        Assert.False(target.Readiness.LifecycleValidationReady);
    }

    [Fact]
    public void EnabledValidationBuildRequiresMatchingTargetAndItsOwnReadyEvidence()
    {
        string revision = new('A', 40);
        string assembly = new('B', 64);
        var ready2026 = new PadlessHyperBoostLifecycleTarget(
            "RZ09-0581", PerformancePreset.Balanced, new string('C', 64),
            HyperBoostWithoutCoolingPadEvidenceGates.UniformForTest(true));
        var pending2025 = new PadlessHyperBoostLifecycleTarget(
            "RZ09-0528", PerformancePreset.Performance, new string('D', 64),
            HyperBoostWithoutCoolingPadEvidenceGates.UniformForTest(false));
        var build2026 = new PadlessHyperBoostLifecycleValidationBuildIdentity(
            true, revision, assembly, ready2026.ManifestSha256);
        var build2025 = build2026 with { AdmissionManifestSha256 = pending2025.ManifestSha256 };

        Assert.True(build2026.Authorizes(ready2026, revision, assembly, ready2026.ManifestSha256));
        Assert.False(build2026.Authorizes(null, revision, assembly, ready2026.ManifestSha256));
        Assert.False(build2025.Authorizes(pending2025, revision, assembly, ready2026.ManifestSha256));
        Assert.False(build2025.Authorizes(pending2025, revision, assembly, pending2025.ManifestSha256));
        Assert.False(build2025.Authorizes(ready2026, revision, assembly, pending2025.ManifestSha256));
        Assert.False(build2026.Authorizes(ready2026, revision, new string('E', 64), ready2026.ManifestSha256));
    }
}
