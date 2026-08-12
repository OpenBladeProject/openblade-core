using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Protocol;

internal sealed record SlimQ330WAdapterAdmissionManifest(
    int SchemaVersion,
    string EvidenceKind,
    SlimQ330WAdapterAdmissionHost Host,
    SlimQ330WAdapterAdmissionAdapter Adapter,
    SlimQ330WAdapterAdmissionEvidence Evidence,
    SlimQ330WAdapterAdmissionGate Gate);

internal sealed record SlimQ330WAdapterAdmissionHost(
    string Manufacturer,
    string Model,
    string Sku,
    IReadOnlyList<string> AdmittedBiosVersions,
    int VendorId,
    int ProductId);

internal sealed record SlimQ330WAdapterAdmissionAdapter(
    string Manufacturer,
    int AdvertisedPowerWatts,
    int CommandClass,
    int CommandId,
    string RequestPayloadHex,
    string ResponsePayloadHex,
    string MappedState);

internal sealed record SlimQ330WAdapterAdmissionEvidence(
    string CaptureCommit,
    string ValidatedBiosVersion,
    string Annotation,
    string AnnotationSha256,
    int IdenticalReadCount,
    int PowerOnlineSampleCount);

internal sealed record SlimQ330WAdapterAdmissionGate(
    bool ExactReadbackValidated,
    bool Bios301CompatibilityAdmissionApproved,
    bool ProductionPowerClassAdmitted);

internal static class SlimQ330WAdapterAdmissionSource
{
    private const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_4_00_SlimQ330WAdapterAdmission.json";
    private const string ExpectedSha256 =
        "9A19B36FFA55FFE61A3F4EFE0D04AC2115E46315B1B45146906967779456125E";
    private const string AnnotationSha256 =
        "6402DFA16478DDB80A6E7CE0442612E34215E4FC05DC400EA83EA2EDE3C148A6";
    private const string CaptureCommit =
        "7cb784d93de51bb8ef3fc6ac0a97fc80010d7b19";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static bool IsAdmitted() =>
        ReadValidated().Gate is
        {
            ExactReadbackValidated: true,
            Bios301CompatibilityAdmissionApproved: true,
            ProductionPowerClassAdmitted: true,
        };

    internal static SlimQ330WAdapterAdmissionManifest ReadValidated()
    {
        Assembly assembly = typeof(SlimQ330WAdapterAdmissionSource).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw Invalid("The embedded SlimQ 330 W adapter admission manifest is missing.");
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, ExpectedSha256, StringComparison.Ordinal))
        {
            throw Invalid(
                "The embedded SlimQ 330 W adapter admission manifest hash is not trusted.");
        }

        SlimQ330WAdapterAdmissionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SlimQ330WAdapterAdmissionManifest>(
                bytes,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"The embedded SlimQ 330 W adapter admission manifest is malformed: {exception.Message}");
        }

        Validate(manifest);
        return manifest!;
    }

    internal static void ValidateForTest(
        SlimQ330WAdapterAdmissionManifest? manifest) => Validate(manifest);

    private static void Validate(SlimQ330WAdapterAdmissionManifest? manifest)
    {
        if (manifest is not
            {
                SchemaVersion: 1,
                EvidenceKind: "slimQ330WAdapterSemanticAdmission",
                Host:
                {
                    Manufacturer: "Razer",
                    Model: "Blade 16 - RZ09-0581",
                    Sku: "RZ09-05819EN4",
                    VendorId: 0x1532,
                    ProductId: 0x02E0,
                },
                Adapter:
                {
                    Manufacturer: "SlimQ",
                    AdvertisedPowerWatts: 330,
                    CommandClass: AdapterPowerProtocol.CommandClass,
                    CommandId: AdapterPowerProtocol.CommandId,
                    RequestPayloadHex: "0000",
                    ResponsePayloadHex: "1211",
                    MappedState: "FullPower330W",
                },
                Evidence:
                {
                    CaptureCommit: CaptureCommit,
                    ValidatedBiosVersion: "4.00",
                    Annotation:
                        "captures/annotations/2026-08-12-rz09-0581-slimq-330w-production-admission.json",
                    AnnotationSha256: AnnotationSha256,
                    IdenticalReadCount: 10,
                    PowerOnlineSampleCount: 4,
                },
                Gate:
                {
                    ExactReadbackValidated: true,
                    Bios301CompatibilityAdmissionApproved: true,
                    ProductionPowerClassAdmitted: true,
                },
            })
        {
            throw Invalid(
                "The embedded SlimQ 330 W adapter admission manifest is not the reviewed exact-device contract.");
        }

        if (!manifest.Host.AdmittedBiosVersions.SequenceEqual(["3.01", "4.00"]))
        {
            throw Invalid(
                "The embedded SlimQ 330 W adapter BIOS admission set is not the reviewed contract.");
        }
    }

    private static UnvalidatedCommandException Invalid(string message) => new(message);
}
