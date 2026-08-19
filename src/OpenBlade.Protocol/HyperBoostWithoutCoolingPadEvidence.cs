using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Protocol;

internal sealed record HyperBoostWithoutCoolingPadAdmissionManifest(
    int SchemaVersion,
    string EvidenceKind,
    HyperBoostWithoutCoolingPadAdmissionHost Host,
    HyperBoostWithoutCoolingPadAdmissionTransition Transition,
    HyperBoostWithoutCoolingPadAdmissionEvidence Evidence,
    HyperBoostWithoutCoolingPadAdmissionGates Gates);

internal sealed record HyperBoostWithoutCoolingPadAdmissionHost(
    string Manufacturer,
    string Model,
    string Sku,
    string BiosVersion,
    int VendorId,
    int ProductId);

internal sealed record HyperBoostWithoutCoolingPadAdmissionTransition(
    string BaselineMode,
    string BaselineFanMode,
    int CandidateMode,
    int CandidateValue,
    int ThermalChannelCount,
    bool FullPowerAdapterRequired,
    bool ExactPadAbsenceRequired,
    IReadOnlyList<string> AdmittedFullPowerAdapterStates,
    bool StrictControllerExclusionRequired,
    bool PairedReadbackRequired,
    bool ExactBalancedRestoreRequired,
    bool IndeterminateRetryForbidden,
    bool PhysicalPadVidPidPresenceVetoRequired);

internal sealed record HyperBoostWithoutCoolingPadAdmissionEvidence(
    string ImmediateAnnotation,
    string ImmediateAnnotationSha256,
    string SustainedNoLoadPlan,
    string SustainedNoLoadPlanSha256,
    string ReviewedLoadPlan,
    string ReviewedLoadPlanSha256,
    string AdapterTransitionPlan,
    string AdapterTransitionPlanSha256);

internal sealed record HyperBoostWithoutCoolingPadAdmissionGates(
    ReviewedAdmissionGate ExactDeviceAndFirmwareReviewed,
    ReviewedAdmissionGate ImmediateNoLoadApplyReadbackRestoreValidated,
    ReviewedAdmissionGate SustainedNoLoadPlanReviewed,
    ReviewedAdmissionGate SustainedNoLoadExecutionValidated,
    ReviewedAdmissionGate PostRestorationAbsenceValidated,
    ReviewedAdmissionGate ReviewedLoadPlanReviewed,
    ReviewedAdmissionGate ReviewedLoadValidated,
    ReviewedAdmissionGate AdapterTransitionValidated,
    ReviewedAdmissionGate LowPowerLifecycleValidated,
    ReviewedAdmissionGate CrashRecoveryValidated);

/// <summary>
/// Evidence gates for enabling HyperBoost while the exact cooling pad is
/// absent. The embedded manifest records developer observations but keeps
/// production mutation closed until sustained and lifecycle validation are
/// complete.
/// </summary>
internal readonly record struct HyperBoostWithoutCoolingPadEvidenceGates(
    bool ExactDeviceAndFirmwareReviewed,
    bool ImmediateNoLoadApplyReadbackRestoreValidated,
    bool SustainedNoLoadPlanReviewed,
    bool SustainedNoLoadExecutionValidated,
    bool PostRestorationAbsenceValidated,
    bool ReviewedLoadPlanReviewed,
    bool ReviewedLoadValidated,
    bool AdapterTransitionValidated,
    bool LowPowerLifecycleValidated,
    bool CrashRecoveryValidated)
{
    internal bool WriteAdmitted =>
        LifecycleValidationReady
        && LowPowerLifecycleValidated
        && CrashRecoveryValidated;

    internal bool LifecycleValidationReady =>
        ExactDeviceAndFirmwareReviewed
        && ImmediateNoLoadApplyReadbackRestoreValidated
        && SustainedNoLoadPlanReviewed
        && SustainedNoLoadExecutionValidated
        && PostRestorationAbsenceValidated
        && ReviewedLoadPlanReviewed
        && ReviewedLoadValidated
        && AdapterTransitionValidated;

    internal static HyperBoostWithoutCoolingPadEvidenceGates Current =>
        HyperBoostWithoutCoolingPadAdmissionSource.ReadGates();

    internal static HyperBoostWithoutCoolingPadEvidenceGates UniformForTest(
        bool admitted) => new(
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted);
}

internal static class HyperBoostWithoutCoolingPadAdmissionSource
{
    private const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_4_00_HyperBoostWithoutCoolingPadAdmission.json";
    private const string ExpectedSha256 =
        "C537F003F4843476ED7EB95F0FC4B1E02673CEC54843ACF5D9C8BD66BC9AE502";

    internal static string TrustedManifestSha256 => ExpectedSha256;
    private const string ImmediateAnnotationSha256 =
        "B1673380EC86030642D0E5F007E55EE12594B89C035C6A216C4503B5010026AB";
    private const string SustainedNoLoadPlanSha256 =
        "2E4837EA0E9B81814FC551954031B5C5BF60A8E3CF593EEA9443684B62B09F33";
    private const string ReviewedLoadPlanSha256 =
        "2D1BBA887F0DC952EEABB39BC3E670A2CE3DA338EC05E46646B1E9F64D780086";
    private const string AdapterTransitionPlanSha256 =
        "95E2333A19118480E6D8193933B7548587BF5C40D151C99BFE81E06D421C7537";
    private const string ExactHeadImmediateAnnotationSha256 =
        "E64A2EB77C254704E3752641EB16BDA9E27BCB0C126167A21071A808F9F5C32A";
    private const string SustainedNoLoadAnnotationSha256 =
        "40E6E6EE1F9EBAC212F72BDF883A5F1CEA065FCECE7916E95DFFE77FCE3EFA61";
    private const string ReviewedLoadAnnotationSha256 =
        "4818729C2E46F7E3B3C308552C3A5E049A441F49B21DF7B45DD88545B81BB00C";
    private const string AdapterTransitionAnnotationSha256 =
        "2BB2912F64E7B6DD1619821F9BD6253CE22C39ACD73AD42CEFA9D70C7DB42874";
    private const string LowPowerLifecycleAnnotationSha256 =
        "7C074F286C2B99D2DE7BAABDA2B0356D14A1B5BB3657C3C165589402BEDF1A91";
    private const string CrashRecoveryAnnotationSha256 =
        "D202CB0BDF516FD9E3B4393FD6FDE28A7916F5909C884FA44829051686652207";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static HyperBoostWithoutCoolingPadEvidenceGates ReadGates()
    {
        HyperBoostWithoutCoolingPadAdmissionManifest manifest = ReadValidated();
        HyperBoostWithoutCoolingPadAdmissionGates gates = manifest.Gates;
        return new HyperBoostWithoutCoolingPadEvidenceGates(
            gates.ExactDeviceAndFirmwareReviewed.Validated,
            gates.ImmediateNoLoadApplyReadbackRestoreValidated.Validated,
            gates.SustainedNoLoadPlanReviewed.Validated,
            gates.SustainedNoLoadExecutionValidated.Validated,
            gates.PostRestorationAbsenceValidated.Validated,
            gates.ReviewedLoadPlanReviewed.Validated,
            gates.ReviewedLoadValidated.Validated,
            gates.AdapterTransitionValidated.Validated,
            gates.LowPowerLifecycleValidated.Validated,
            gates.CrashRecoveryValidated.Validated);
    }

    internal static HyperBoostWithoutCoolingPadAdmissionManifest ReadValidated()
    {
        Assembly assembly = typeof(HyperBoostWithoutCoolingPadAdmissionSource).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw Invalid("The embedded pad-less HyperBoost admission manifest is missing.");
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, ExpectedSha256, StringComparison.Ordinal))
        {
            throw Invalid(
                "The embedded pad-less HyperBoost admission manifest hash is not trusted.");
        }

        HyperBoostWithoutCoolingPadAdmissionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<
                HyperBoostWithoutCoolingPadAdmissionManifest>(
                bytes,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"The embedded pad-less HyperBoost admission manifest is malformed: {exception.Message}");
        }

        Validate(manifest);
        return manifest!;
    }

    internal static void ValidateForTest(
        HyperBoostWithoutCoolingPadAdmissionManifest? manifest) =>
        Validate(manifest);

    private static void Validate(
        HyperBoostWithoutCoolingPadAdmissionManifest? manifest)
    {
        if (manifest is not
            {
                SchemaVersion: 3,
                EvidenceKind: "hyperBoostWithoutCoolingPadSemanticAdmission",
                Host:
                {
                    Manufacturer: "Razer",
                    Model: "Blade 16 - RZ09-0581",
                    Sku: "RZ09-05819EN4",
                    BiosVersion: "4.00",
                    VendorId: 0x1532,
                    ProductId: 0x02E0,
                },
                Transition:
                {
                    BaselineMode: "Balanced",
                    BaselineFanMode: "Automatic",
                    CandidateMode: 7,
                    CandidateValue: 0,
                    ThermalChannelCount: 2,
                    FullPowerAdapterRequired: true,
                    ExactPadAbsenceRequired: true,
                    AdmittedFullPowerAdapterStates:
                        ["FullPower280W", "FullPower330W"],
                    StrictControllerExclusionRequired: true,
                    PairedReadbackRequired: true,
                    ExactBalancedRestoreRequired: true,
                    IndeterminateRetryForbidden: true,
                    PhysicalPadVidPidPresenceVetoRequired: true,
                },
                Evidence:
                {
                    ImmediateAnnotation:
                        "2026-08-09-rz09-0581-hyperboost-pad-absent-validation.json",
                    ImmediateAnnotationSha256: ImmediateAnnotationSha256,
                    SustainedNoLoadPlan:
                        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostNoLoadPlan.json",
                    SustainedNoLoadPlanSha256: SustainedNoLoadPlanSha256,
                    ReviewedLoadPlan:
                        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostExternalLoadPlan.json",
                    ReviewedLoadPlanSha256: ReviewedLoadPlanSha256,
                    AdapterTransitionPlan:
                        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostAdapterTransitionPlan.json",
                    AdapterTransitionPlanSha256: AdapterTransitionPlanSha256,
                },
                Gates:
                {
                    ExactDeviceAndFirmwareReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-09-rz09-0581-hyperboost-pad-absent-validation.json",
                            Sha256: ImmediateAnnotationSha256,
                        },
                    },
                    ImmediateNoLoadApplyReadbackRestoreValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-immediate.json",
                            Sha256: ExactHeadImmediateAnnotationSha256,
                        },
                    },
                    SustainedNoLoadPlanReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_4_00_PadAbsentHyperBoostNoLoadPlan.json",
                            Sha256: SustainedNoLoadPlanSha256,
                        },
                    },
                    SustainedNoLoadExecutionValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-sustained-no-load.json",
                            Sha256: SustainedNoLoadAnnotationSha256,
                        },
                    },
                    PostRestorationAbsenceValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-immediate.json",
                            Sha256: ExactHeadImmediateAnnotationSha256,
                        },
                    },
                    ReviewedLoadPlanReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_4_00_PadAbsentHyperBoostExternalLoadPlan.json",
                            Sha256: ReviewedLoadPlanSha256,
                        },
                    },
                    ReviewedLoadValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-steel-nomad.json",
                            Sha256: ReviewedLoadAnnotationSha256,
                        },
                    },
                    AdapterTransitionValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-adapter-transition.json",
                            Sha256: AdapterTransitionAnnotationSha256,
                        },
                    },
                    LowPowerLifecycleValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-low-power-recovery.json",
                            Sha256: LowPowerLifecycleAnnotationSha256,
                        },
                    },
                    CrashRecoveryValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-crash-recovery.json",
                            Sha256: CrashRecoveryAnnotationSha256,
                        },
                    },
                },
            })
        {
            throw Invalid(
                "The embedded pad-less HyperBoost admission manifest does not match the exact reviewed semantic contract.");
        }

        if (!ReviewedAdmissionGate.AllWellFormed(
            manifest.Gates.ExactDeviceAndFirmwareReviewed,
            manifest.Gates.ImmediateNoLoadApplyReadbackRestoreValidated,
            manifest.Gates.SustainedNoLoadPlanReviewed,
            manifest.Gates.SustainedNoLoadExecutionValidated,
            manifest.Gates.PostRestorationAbsenceValidated,
            manifest.Gates.ReviewedLoadPlanReviewed,
            manifest.Gates.ReviewedLoadValidated,
            manifest.Gates.AdapterTransitionValidated,
            manifest.Gates.LowPowerLifecycleValidated,
            manifest.Gates.CrashRecoveryValidated))
        {
            throw Invalid(
                "Every pad-less HyperBoost admission gate must bind exact reviewed evidence.");
        }
    }

    private static UnvalidatedCommandException Invalid(string message) => new(message);
}
