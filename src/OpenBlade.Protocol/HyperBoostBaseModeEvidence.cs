using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record HyperBoostBaseModeAdmissionManifest(
    int SchemaVersion,
    string EvidenceKind,
    HyperBoostBaseModeAdmissionHost Host,
    HyperBoostBaseModeAdmissionTransition Transition,
    HyperBoostBaseModeAdmissionEvidence Evidence,
    HyperBoostBaseModeAdmissionModes BaseModes);

internal sealed record HyperBoostBaseModeAdmissionHost(
    string Manufacturer,
    string Model,
    string Sku,
    string BiosVersion,
    int VendorId,
    int ProductId);

internal sealed record HyperBoostBaseModeAdmissionTransition(
    int CandidateMode,
    int CandidateValue,
    int ThermalChannelCount,
    string BaselineFanMode,
    bool FullPowerAdapterRequired,
    bool ExactCoolingPadRequired,
    bool BalancedIntermediaryAllowed);

internal sealed record HyperBoostBaseModeAdmissionEvidence(
    string ProductionFixture,
    string ProductionFixtureSha256,
    string BalancedValidationAnnotation,
    string BalancedValidationAnnotationSha256,
    string BalancedRepeatAnnotation,
    string BalancedRepeatAnnotationSha256);

internal sealed record HyperBoostBaseModeAdmissionModes(
    HyperBoostBaseModeAdmissionGate Silent,
    HyperBoostBaseModeAdmissionGate Balanced,
    HyperBoostBaseModeAdmissionGate Performance);

internal sealed record HyperBoostBaseModeAdmissionGate(
    bool DirectTransitionImplemented,
    ReviewedAdmissionGate ExactApplyReadbackRestore);

internal static class HyperBoostBaseModeAdmissionSource
{
    private const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_4_00_HyperBoostBaseModesAdmission.json";
    private const string ExpectedSha256 =
        "0F6B6DE3525A63F3F27716FD37F137AA68503644A2CA157EDD8431ED5265CFB0";
    private const string ProductionFixtureSha256 =
        "3DBE55C2E2972EE2C97FE5DC5D7A53D7F175CF37EB954E95C16E67589401010D";
    private const string BalancedValidationAnnotationSha256 =
        "03FE24FC3FDC67955DC7F86D1612988FF72C1923FBA11017B369715DB41B50B9";
    private const string BalancedRepeatAnnotationSha256 =
        "DA96FF79C1EDED5F1F2A414CF25F8F664A93D7E54253EDB16DB4D36407E493D3";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static HyperBoostBaseModeCapabilities ReadCapabilities()
    {
        HyperBoostBaseModeAdmissionModes modes = ReadValidated().BaseModes;
        return new HyperBoostBaseModeCapabilities(
            AccessFor(modes.Silent),
            AccessFor(modes.Balanced),
            AccessFor(modes.Performance));
    }

    internal static HyperBoostBaseModeAdmissionManifest ReadValidated()
    {
        Assembly assembly = typeof(HyperBoostBaseModeAdmissionSource).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw Invalid(
                "The embedded HyperBoost base-mode admission manifest is missing.");
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, ExpectedSha256, StringComparison.Ordinal))
        {
            throw Invalid(
                "The embedded HyperBoost base-mode admission manifest hash is not trusted.");
        }

        HyperBoostBaseModeAdmissionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<HyperBoostBaseModeAdmissionManifest>(
                bytes,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"The embedded HyperBoost base-mode admission manifest is malformed: {exception.Message}");
        }

        Validate(manifest);
        return manifest!;
    }

    private static DeviceFeatureAccess AccessFor(
        HyperBoostBaseModeAdmissionGate gate) =>
        gate.DirectTransitionImplemented
            ? gate.ExactApplyReadbackRestore.Validated
                ? DeviceFeatureAccess.ReadWrite
                : DeviceFeatureAccess.ObservedOnly
            : throw Invalid(
                "Every advertised HyperBoost base mode must have a typed direct transition implementation.");

    internal static void ValidateForTest(
        HyperBoostBaseModeAdmissionManifest? manifest) => Validate(manifest);

    private static void Validate(HyperBoostBaseModeAdmissionManifest? manifest)
    {
        if (manifest is not
            {
                SchemaVersion: 2,
                EvidenceKind: "hyperBoostBaseModeSemanticAdmission",
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
                    CandidateMode: 7,
                    CandidateValue: 0,
                    ThermalChannelCount: 2,
                    BaselineFanMode: "Automatic",
                    FullPowerAdapterRequired: true,
                    ExactCoolingPadRequired: true,
                    BalancedIntermediaryAllowed: false,
                },
                Evidence:
                {
                    ProductionFixture: "Rz09_0581_02e0_4_00_HyperBoostProduction.json",
                    ProductionFixtureSha256: ProductionFixtureSha256,
                    BalancedValidationAnnotation: "2026-08-09-rz09-0581-hyperboost-write-validation.json",
                    BalancedValidationAnnotationSha256: BalancedValidationAnnotationSha256,
                    BalancedRepeatAnnotation: "2026-08-09-rz09-0581-hyperboost-write-validation-repeat.json",
                    BalancedRepeatAnnotationSha256: BalancedRepeatAnnotationSha256,
                },
                BaseModes:
                {
                    Silent:
                    {
                        DirectTransitionImplemented: true,
                        ExactApplyReadbackRestore:
                        {
                            Validated: false,
                            Evidence: null,
                        },
                    },
                    Balanced:
                    {
                        DirectTransitionImplemented: true,
                        ExactApplyReadbackRestore:
                        {
                            Validated: true,
                            Evidence:
                            {
                                Artifact: "captures/annotations/2026-08-09-rz09-0581-hyperboost-write-validation.json",
                                Sha256: BalancedValidationAnnotationSha256,
                            },
                        },
                    },
                    Performance:
                    {
                        DirectTransitionImplemented: true,
                        ExactApplyReadbackRestore:
                        {
                            Validated: false,
                            Evidence: null,
                        },
                    },
                },
            })
        {
            throw Invalid(
                "The embedded HyperBoost base-mode admission manifest is not the reviewed exact-device contract.");
        }

        if (!ReviewedAdmissionGate.AllWellFormed(
            manifest.BaseModes.Silent.ExactApplyReadbackRestore,
            manifest.BaseModes.Balanced.ExactApplyReadbackRestore,
            manifest.BaseModes.Performance.ExactApplyReadbackRestore))
        {
            throw Invalid(
                "Every HyperBoost base-mode admission gate must bind exact reviewed evidence.");
        }
    }

    private static UnvalidatedCommandException Invalid(string message) => new(message);
}
