using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Protocol;

internal sealed record Rz090528HyperBoostWithoutCoolingPadAdmissionManifest(
    int SchemaVersion,
    string EvidenceKind,
    HyperBoostWithoutCoolingPadAdmissionHost Host,
    Rz090528HyperBoostWithoutCoolingPadAdmissionTransition Transition,
    Rz090528HyperBoostWithoutCoolingPadAdmissionEvidence Evidence,
    HyperBoostWithoutCoolingPadAdmissionGates Gates);

internal sealed record Rz090528HyperBoostWithoutCoolingPadAdmissionTransition(
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
    bool ExactPerformanceRestoreRequired,
    bool IndeterminateRetryForbidden,
    bool PhysicalPadVidPidPresenceVetoRequired,
    bool GpuClockTuningExcluded,
    bool CoolingPadPathRetained);

internal sealed record Rz090528HyperBoostWithoutCoolingPadAdmissionEvidence(
    string CaptureBiosVersion,
    string ReadinessDescriptor,
    string ReadinessDescriptorSha256,
    string SupplementalAdapterTransitionAnnotation,
    string SupplementalAdapterTransitionAnnotationSha256,
    string Decision);

/// <summary>
/// Production admission for pad-less HyperBoost on the exact Blade 16 (2025)
/// RZ09-0528. It binds the reviewed Performance/Automatic to mode 7 transition
/// and every 2025 gate artifact; the RZ09-0581 manifest cannot authorize this model.
/// </summary>
internal static class Rz090528HyperBoostWithoutCoolingPadAdmissionSource
{
    private const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_HyperBoostWithoutCoolingPadAdmission.json";
    private const string ExpectedSha256 =
        "13AEF5172C00382EBE4613FAC6675822E0F1D464A3F4B80AB254A0AAA539DEBF";
    private const string ImmediateAnnotationSha256 =
        "AFFD4F85C5677B747009A42B3AD0DEFBDFAFC7A255B7B5962140AFE1D77BCB92";
    private const string SustainedNoLoadPlanSha256 =
        "7C5F8DA522F2DEBA865D4128E9D485D152E41F6E7AB0B9DE5847E6A1D88F424E";
    private const string SustainedNoLoadAnnotationSha256 =
        "8D079A96AEC545665B05D1564FCF5663311651B845A34F236DC98CC8DE0DF997";
    private const string PostRestorationAbsenceAnnotationSha256 =
        "9314C72E868E4D2C025E569E03B6152EC460EE4D68EBBB564056604A99AA5174";
    private const string ReviewedLoadPlanSha256 =
        "AA1C670D89373C3823236BDFB6458747F2002DB8175EBE325D6845C9529ABDF7";
    private const string ReviewedLoadAnnotationSha256 =
        "83A14BCBABF41DF1C961312D85D3DB3B1C8B5B3586E3576D3039423439473538";
    private const string BatteryBoundaryAnnotationSha256 =
        "EC8A775C3040D90EF0FCD6AFF07FAC8B6231A65E7E05F99EA6A39725BCD51CE3";
    private const string UsbCBoundaryAnnotationSha256 =
        "BADBF8D3E135ADD8F3EC4D8DAFEA75EBBAD8B87C7895B151B9F5D508C7224C7C";
    private const string LowPowerLifecycleAnnotationSha256 =
        "8CD05A30F2A5A7CEFB3D627808AD702338FE824B51EB37F7096A57A5BA9FD65B";
    private const string CrashRecoveryAnnotationSha256 =
        "7CD8C237B4092018EE418F859AEAFCE5CA1E0C96554FDC6A72A865888A0B8B73";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static string TrustedManifestSha256 => ExpectedSha256;

    internal static HyperBoostWithoutCoolingPadEvidenceGates ReadGates()
    {
        HyperBoostWithoutCoolingPadAdmissionGates gates = ReadValidated().Gates;
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

    internal static Rz090528HyperBoostWithoutCoolingPadAdmissionManifest ReadValidated()
    {
        using Stream stream = typeof(Rz090528HyperBoostWithoutCoolingPadAdmissionSource).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw Invalid("The embedded RZ09-0528 pad-less HyperBoost admission manifest is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                ExpectedSha256,
                StringComparison.Ordinal))
        {
            throw Invalid("The embedded RZ09-0528 pad-less HyperBoost admission manifest hash is not trusted.");
        }

        Rz090528HyperBoostWithoutCoolingPadAdmissionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Rz090528HyperBoostWithoutCoolingPadAdmissionManifest>(
                bytes,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"The embedded RZ09-0528 pad-less HyperBoost admission manifest is malformed: {exception.Message}");
        }

        Validate(manifest);
        return manifest!;
    }

    internal static void ValidateForTest(
        Rz090528HyperBoostWithoutCoolingPadAdmissionManifest? manifest) =>
        Validate(manifest);

    private static void Validate(Rz090528HyperBoostWithoutCoolingPadAdmissionManifest? manifest)
    {
        if (manifest is not
            {
                SchemaVersion: 1,
                EvidenceKind: "hyperBoostWithoutCoolingPadSemanticAdmission",
                Host:
                {
                    Manufacturer: "Razer",
                    Model: "Blade 16 - RZ09-0528",
                    Sku: "RZ09-05289EN4",
                    FirmwareAdmission: "ModelScoped",
                    VendorId: 0x1532,
                    ProductId: 0x02C6,
                },
                Transition:
                {
                    BaselineMode: "Performance",
                    BaselineFanMode: "Automatic",
                    CandidateMode: 7,
                    CandidateValue: 0,
                    ThermalChannelCount: 2,
                    FullPowerAdapterRequired: true,
                    ExactPadAbsenceRequired: true,
                    AdmittedFullPowerAdapterStates: ["FullPower280W"],
                    StrictControllerExclusionRequired: true,
                    PairedReadbackRequired: true,
                    ExactPerformanceRestoreRequired: true,
                    IndeterminateRetryForbidden: true,
                    PhysicalPadVidPidPresenceVetoRequired: true,
                    GpuClockTuningExcluded: false,
                    CoolingPadPathRetained: true,
                },
                Evidence:
                {
                    CaptureBiosVersion: "2.02",
                    ReadinessDescriptor: "Rz09_0528_02c6_2_02_PadlessLifecyclePreparation.json",
                    ReadinessDescriptorSha256: Rz090528PadlessLifecyclePreparation.ManifestSha256,
                    SupplementalAdapterTransitionAnnotation:
                        "2026-09-28-rz09-0528-padless-hyperboost-usb-c-boundary.json",
                    SupplementalAdapterTransitionAnnotationSha256: UsbCBoundaryAnnotationSha256,
                    Decision: "Prepared at the user's request with the cooling-pad path retained; tracked on OpenBladeProject/openblade-core#608",
                },
                Gates:
                {
                    ExactDeviceAndFirmwareReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-27-rz09-0528-padless-hyperboost-immediate.json",
                            Sha256: ImmediateAnnotationSha256,
                        },
                    },
                    ImmediateNoLoadApplyReadbackRestoreValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-27-rz09-0528-padless-hyperboost-immediate.json",
                            Sha256: ImmediateAnnotationSha256,
                        },
                    },
                    SustainedNoLoadPlanReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "src/OpenBlade.Protocol/Fixtures/Rz09_0528_02c6_2_02_PadlessHyperBoostNoLoad60Plan.json",
                            Sha256: SustainedNoLoadPlanSha256,
                        },
                    },
                    SustainedNoLoadExecutionValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-29-rz09-0528-padless-hyperboost-no-load-sixty-second.json",
                            Sha256: SustainedNoLoadAnnotationSha256,
                        },
                    },
                    PostRestorationAbsenceValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-29-rz09-0528-padless-post-restoration-absence.json",
                            Sha256: PostRestorationAbsenceAnnotationSha256,
                        },
                    },
                    ReviewedLoadPlanReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "src/OpenBlade.Protocol/Fixtures/Rz09_0528_02c6_2_02_PadlessHyperBoostLoaded60Plan.json",
                            Sha256: ReviewedLoadPlanSha256,
                        },
                    },
                    ReviewedLoadValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-29-rz09-0528-padless-hyperboost-stress-test-sixty-second.json",
                            Sha256: ReviewedLoadAnnotationSha256,
                        },
                    },
                    AdapterTransitionValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-28-rz09-0528-padless-hyperboost-battery-boundary.json",
                            Sha256: BatteryBoundaryAnnotationSha256,
                        },
                    },
                    LowPowerLifecycleValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-29-rz09-0528-padless-hyperboost-low-power-lifecycle.json",
                            Sha256: LowPowerLifecycleAnnotationSha256,
                        },
                    },
                    CrashRecoveryValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-09-29-rz09-0528-padless-hyperboost-crash-recovery-lifecycle.json",
                            Sha256: CrashRecoveryAnnotationSha256,
                        },
                    },
                },
            })
        {
            throw Invalid(
                "The embedded RZ09-0528 pad-less HyperBoost admission manifest does not match the exact reviewed semantic contract.");
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
            throw Invalid("Every RZ09-0528 pad-less HyperBoost admission gate must bind exact reviewed evidence.");
        }
    }

    private static UnvalidatedCommandException Invalid(string message) => new(message);
}
