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
    string AdapterTransitionPlan,
    string AdapterTransitionPlanSha256);

internal sealed record HyperBoostWithoutCoolingPadAdmissionGates(
    ReviewedAdmissionGate ExactDeviceAndFirmwareReviewed,
    ReviewedAdmissionGate ImmediateNoLoadApplyReadbackRestoreValidated,
    ReviewedAdmissionGate SustainedNoLoadPlanReviewed,
    ReviewedAdmissionGate SustainedNoLoadExecutionValidated,
    ReviewedAdmissionGate PostRestorationAbsenceValidated,
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
        admitted);
}

internal static class HyperBoostWithoutCoolingPadAdmissionSource
{
    private const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_4_00_HyperBoostWithoutCoolingPadAdmission.json";
    private const string ExpectedSha256 =
        "4B6C23C5061EEE5A24BA75A740D893B1882070BA9EFE995BDDD7C49F54D69E83";

    internal static string TrustedManifestSha256 => ExpectedSha256;
    private const string ImmediateAnnotationSha256 =
        "B1673380EC86030642D0E5F007E55EE12594B89C035C6A216C4503B5010026AB";
    private const string SustainedNoLoadPlanSha256 =
        "589A94BCE7050BA43195BBFE5FAC4E8EDF337D481705047C668460ACAB675D2B";
    private const string AdapterTransitionPlanSha256 =
        "95E2333A19118480E6D8193933B7548587BF5C40D151C99BFE81E06D421C7537";

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
                SchemaVersion: 2,
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
                        Validated: false,
                        Evidence: null,
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
                        Validated: false,
                        Evidence: null,
                    },
                    PostRestorationAbsenceValidated:
                    {
                        Validated: false,
                        Evidence: null,
                    },
                    ReviewedLoadValidated:
                    {
                        Validated: false,
                        Evidence: null,
                    },
                    AdapterTransitionValidated:
                    {
                        Validated: false,
                        Evidence: null,
                    },
                    LowPowerLifecycleValidated:
                    {
                        Validated: false,
                        Evidence: null,
                    },
                    CrashRecoveryValidated:
                    {
                        Validated: false,
                        Evidence: null,
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
