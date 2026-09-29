using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528HyperBoostWithoutCoolingPadAdmissionTests
{
    [Fact]
    public void EmbeddedManifest_BindsTheExact2025PerformanceTransition()
    {
        Rz090528HyperBoostWithoutCoolingPadAdmissionManifest manifest =
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ReadValidated();

        Assert.Equal("Blade 16 - RZ09-0528", manifest.Host.Model);
        Assert.Equal("RZ09-05289EN4", manifest.Host.Sku);
        Assert.Equal("ModelScoped", manifest.Host.FirmwareAdmission);
        Assert.Equal(0x02C6, manifest.Host.ProductId);
        Assert.Equal("2.02", manifest.Evidence.CaptureBiosVersion);
        Assert.Equal("Performance", manifest.Transition.BaselineMode);
        Assert.Equal("Automatic", manifest.Transition.BaselineFanMode);
        Assert.Equal(["FullPower280W"], manifest.Transition.AdmittedFullPowerAdapterStates);
        Assert.True(manifest.Transition.ExactPerformanceRestoreRequired);
        Assert.False(manifest.Transition.GpuClockTuningExcluded);
        Assert.True(manifest.Transition.CoolingPadPathRetained);
    }

    [Fact]
    public void CurrentEvidence_AdmitsPadlessWritesOnlyThroughThe2025Manifest()
    {
        HyperBoostWithoutCoolingPadEvidenceGates gates =
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ReadGates();

        Assert.True(gates.WriteAdmitted);
        Assert.Equal(gates, HyperBoostWithoutCoolingPadEvidenceGates.ForModel(RazerBladeModelCatalog.Rz090528ModelNumber));
        Assert.Equal(
            HyperBoostWithoutCoolingPadEvidenceGates.Current,
            HyperBoostWithoutCoolingPadEvidenceGates.ForModel(RazerBladeModelCatalog.OpenBladeTargetModelNumber));
        Assert.Null(HyperBoostWithoutCoolingPadEvidenceGates.ForModel("RZ09-0482"));
        Assert.Null(HyperBoostWithoutCoolingPadEvidenceGates.ForModel(null));
        Assert.NotEqual(
            HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256,
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256);
    }

    [Fact]
    public void EmbeddedManifest_MatchesTheCommittedFixtureBytes()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(
            RepositoryRoot(), "src", "OpenBlade.Protocol", "Fixtures",
            "Rz09_0528_02c6_2_02_HyperBoostWithoutCoolingPadAdmission.json"));

        Assert.Equal(
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void EveryCitedArtifactMatchesTheRepositoryBytes()
    {
        Rz090528HyperBoostWithoutCoolingPadAdmissionManifest manifest =
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ReadValidated();
        var cited = new List<AdmissionEvidenceReference>(AllGates(manifest.Gates).Select(gate => gate.Evidence!))
        {
            new("src/OpenBlade.Protocol/Fixtures/" + manifest.Evidence.ReadinessDescriptor,
                manifest.Evidence.ReadinessDescriptorSha256),
            new("captures/annotations/" + manifest.Evidence.SupplementalAdapterTransitionAnnotation,
                manifest.Evidence.SupplementalAdapterTransitionAnnotationSha256),
        };

        foreach (AdmissionEvidenceReference reference in cited)
        {
            string path = Path.Combine(RepositoryRoot(), reference.Artifact.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), reference.Artifact);
            Assert.Equal(reference.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        Assert.Equal(12, cited.Count);
    }

    [Fact]
    public void NonLifecycleGates_ReuseTheReviewedReadinessBindings()
    {
        HyperBoostWithoutCoolingPadAdmissionGates gates =
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ReadValidated().Gates;
        using JsonDocument readiness = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RepositoryRoot(), "src", "OpenBlade.Protocol", "Fixtures",
            "Rz09_0528_02c6_2_02_PadlessLifecyclePreparation.json")));
        JsonElement bound = readiness.RootElement.GetProperty("gates");
        string[] names = Rz090528PadlessLifecyclePreparation.GateNames;
        ReviewedAdmissionGate[] admitted = AllGates(gates);

        for (int index = 0; index < 8; index++)
        {
            JsonElement evidence = bound.GetProperty(names[index]).GetProperty("evidence");
            Assert.Equal(evidence.GetProperty("artifact").GetString(), admitted[index].Evidence!.Artifact);
            Assert.Equal(evidence.GetProperty("sha256").GetString(), admitted[index].Evidence!.Sha256);
        }
    }

    [Fact]
    public void LifecycleGates_CiteTheInstalledRunsOfOneGeneration()
    {
        HyperBoostWithoutCoolingPadAdmissionGates gates =
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ReadValidated().Gates;
        JsonElement lowPower = ReadAnnotation(gates.LowPowerLifecycleValidated.Evidence!);
        JsonElement crash = ReadAnnotation(gates.CrashRecoveryValidated.Evidence!);

        Assert.Equal("InstalledLowPowerLifecycle", lowPower.GetProperty("evidenceKind").GetString());
        Assert.Equal("InstalledUnexpectedExitLifecycle", crash.GetProperty("evidenceKind").GetString());
        Assert.True(lowPower.GetProperty("interpretation").GetProperty("lowPowerLifecycleValidated").GetBoolean());
        Assert.True(crash.GetProperty("interpretation").GetProperty("crashRecoveryValidated").GetBoolean());
        Assert.True(lowPower.GetProperty("validation").GetProperty("verification").GetProperty("success").GetBoolean());
        Assert.True(crash.GetProperty("validation").GetProperty("verification").GetProperty("success").GetBoolean());
        Assert.Equal(
            crash.GetProperty("evidenceProvenance").GetProperty("installedGenerationSha256").GetString(),
            lowPower.GetProperty("evidenceProvenance").GetProperty("installedGenerationSha256").GetString());
        Assert.Equal("RZ09-0528", lowPower.GetProperty("device").GetProperty("modelNumber").GetString());
        Assert.Equal("RZ09-0528", crash.GetProperty("device").GetProperty("modelNumber").GetString());
    }

    public static TheoryData<string> Mutations => new()
    {
        "balanced-baseline",
        "330w-adapter",
        "other-host",
        "gpu-tuning-excluded",
        "pad-path-dropped",
        "lifecycle-gate-open",
        "lifecycle-evidence-hash",
        "readiness-hash",
        "ungated-evidence",
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public void SemanticContract_RejectsPlausibleChanges(string mutation)
    {
        Rz090528HyperBoostWithoutCoolingPadAdmissionManifest manifest =
            Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ReadValidated();
        HyperBoostWithoutCoolingPadAdmissionGates gates = manifest.Gates;
        Rz090528HyperBoostWithoutCoolingPadAdmissionManifest changed = mutation switch
        {
            "balanced-baseline" => manifest with { Transition = manifest.Transition with { BaselineMode = "Balanced" } },
            "330w-adapter" => manifest with
            {
                Transition = manifest.Transition with
                {
                    AdmittedFullPowerAdapterStates = ["FullPower280W", "FullPower330W"],
                },
            },
            "other-host" => manifest with { Host = manifest.Host with { Model = "Blade 16 - RZ09-0581", ProductId = 0x02E0 } },
            "gpu-tuning-excluded" => manifest with { Transition = manifest.Transition with { GpuClockTuningExcluded = true } },
            "pad-path-dropped" => manifest with { Transition = manifest.Transition with { CoolingPadPathRetained = false } },
            "lifecycle-gate-open" => manifest with
            {
                Gates = gates with { CrashRecoveryValidated = new ReviewedAdmissionGate(false, null) },
            },
            "lifecycle-evidence-hash" => manifest with
            {
                Gates = gates with
                {
                    LowPowerLifecycleValidated = gates.LowPowerLifecycleValidated with
                    {
                        Evidence = gates.LowPowerLifecycleValidated.Evidence! with { Sha256 = new string('0', 64) },
                    },
                },
            },
            "readiness-hash" => manifest with
            {
                Evidence = manifest.Evidence with { ReadinessDescriptorSha256 = new string('0', 64) },
            },
            "ungated-evidence" => manifest with
            {
                Gates = gates with { AdapterTransitionValidated = new ReviewedAdmissionGate(true, null) },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.Throws<UnvalidatedCommandException>(
            () => Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ValidateForTest(changed));
    }

    private static ReviewedAdmissionGate[] AllGates(HyperBoostWithoutCoolingPadAdmissionGates gates) =>
    [
        gates.ExactDeviceAndFirmwareReviewed,
        gates.ImmediateNoLoadApplyReadbackRestoreValidated,
        gates.SustainedNoLoadPlanReviewed,
        gates.SustainedNoLoadExecutionValidated,
        gates.PostRestorationAbsenceValidated,
        gates.ReviewedLoadPlanReviewed,
        gates.ReviewedLoadValidated,
        gates.AdapterTransitionValidated,
        gates.LowPowerLifecycleValidated,
        gates.CrashRecoveryValidated,
    ];

    private static JsonElement ReadAnnotation(AdmissionEvidenceReference reference) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RepositoryRoot(), reference.Artifact.Replace('/', Path.DirectorySeparatorChar)))).RootElement.Clone();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx"))) { return directory.FullName; }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
