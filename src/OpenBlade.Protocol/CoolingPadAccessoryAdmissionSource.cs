using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record CoolingPadAccessoryAdmissionManifest(
    int SchemaVersion,
    string EvidenceKind,
    CoolingPadAdmissionHost Host,
    CoolingPadAdmissionDevice Device,
    CoolingPadFanAdmissionManifest Fan,
    CoolingPadLightingAdmissionManifest Lighting);

internal sealed record CoolingPadAdmissionHost(
    string Manufacturer,
    string Model,
    string Sku);

internal sealed record CoolingPadAdmissionDevice(
    int VendorId,
    int ProductId,
    int UsagePage,
    int Usage,
    int FeatureReportLength,
    int ReleaseNumber,
    int FeatureReportId,
    int ControlReportLength);

internal sealed record CoolingPadFanAdmissionManifest(
    string OpaqueQuery,
    string HostControlPreparation,
    string HostDemand2200,
    ReviewedAdmissionGate ExactIdentityAndTransportReviewed,
    ReviewedAdmissionGate HostControl2200PairPhysicallyValidated,
    ReviewedAdmissionGate OpenBladeHandbackValidated,
    ReviewedAdmissionGate FullTargetRangeValidated,
    ReviewedAdmissionGate SmartCurveCadenceValidated,
    ReviewedAdmissionGate DisconnectReconnectValidated,
    ReviewedAdmissionGate LowPowerLifecycleValidated,
    ReviewedAdmissionGate CrashRecoveryValidated,
    ReviewedAdmissionGate HyperBoostInterlockValidated,
    ReviewedAdmissionGate PhysicalButtonOwnershipPolicyValidated);

internal sealed record CoolingPadLightingAdmissionManifest(
    string FrameCommand,
    int FramePayloadLength,
    int LogicalSlotCount,
    string UniformKnownAnswerColor,
    string UniformKnownAnswerReportSha256,
    string BrightnessQuery,
    string BrightnessSetPrefix,
    ReviewedAdmissionGate ExactIdentityAndGeometryReviewed,
    ReviewedAdmissionGate WholeStripProjectionResolved,
    ReviewedAdmissionGate DisplayOffOracleObserved,
    ReviewedAdmissionGate OpenBladeReversibleFrameLifecycleValidated,
    ReviewedAdmissionGate FrameCadenceValidated,
    ReviewedAdmissionGate DisconnectReconnectValidated,
    ReviewedAdmissionGate SleepResumeValidated,
    ReviewedAdmissionGate ControllerHandoffValidated);

internal static class CoolingPadAccessoryAdmissionSource
{
    private const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_0f43_CoolingPadSemanticAdmission.json";
    private const string ExpectedSha256 =
        "B7D4537E62314572D40B508E248DBE0DB5E98395FD11A6215641ECF2C90FCF1A";
    private const string KnownAnswerReportSha256 =
        "2E3F0F802124D7933D499AEBEAB760F6C026DBDB2C639E2E5448FBB006CF1A07";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static CoolingPadAccessoryAdmissionManifest ReadValidated()
    {
        Assembly assembly = typeof(CoolingPadAccessoryAdmissionSource).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw Invalid("The embedded cooling-pad admission manifest is missing.");
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, ExpectedSha256, StringComparison.Ordinal))
        {
            throw Invalid("The embedded cooling-pad admission manifest hash is not trusted.");
        }

        CoolingPadAccessoryAdmissionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CoolingPadAccessoryAdmissionManifest>(
                bytes,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"The embedded cooling-pad admission manifest is malformed: {exception.Message}");
        }

        Validate(manifest);
        return manifest!;
    }

    internal static CoolingPadFanControlEvidenceGates ReadFanGates()
    {
        CoolingPadFanAdmissionManifest fan = ReadValidated().Fan;
        return new CoolingPadFanControlEvidenceGates(
            fan.ExactIdentityAndTransportReviewed.Validated,
            fan.HostControl2200PairPhysicallyValidated.Validated,
            fan.OpenBladeHandbackValidated.Validated,
            fan.FullTargetRangeValidated.Validated,
            fan.SmartCurveCadenceValidated.Validated,
            fan.DisconnectReconnectValidated.Validated,
            fan.LowPowerLifecycleValidated.Validated,
            fan.CrashRecoveryValidated.Validated,
            fan.HyperBoostInterlockValidated.Validated,
            fan.PhysicalButtonOwnershipPolicyValidated.Validated);
    }

    internal static CoolingPadLightingEvidenceGates ReadLightingGates()
    {
        CoolingPadLightingAdmissionManifest lighting = ReadValidated().Lighting;
        return new CoolingPadLightingEvidenceGates(
            lighting.ExactIdentityAndGeometryReviewed.Validated,
            lighting.WholeStripProjectionResolved.Validated,
            lighting.DisplayOffOracleObserved.Validated,
            lighting.OpenBladeReversibleFrameLifecycleValidated.Validated,
            lighting.FrameCadenceValidated.Validated,
            lighting.DisconnectReconnectValidated.Validated,
            lighting.SleepResumeValidated.Validated,
            lighting.ControllerHandoffValidated.Validated);
    }

    internal static void ValidateForTest(
        CoolingPadAccessoryAdmissionManifest? manifest) => Validate(manifest);

    private static void Validate(CoolingPadAccessoryAdmissionManifest? manifest)
    {
        if (manifest is not
            {
                SchemaVersion: 2,
                EvidenceKind: "coolingPadSemanticAdmission",
                Host:
                {
                    Manufacturer: "Razer",
                    Model: "Blade 16 - RZ09-0581",
                    Sku: "RZ09-05819EN4",
                },
                Device:
                {
                    VendorId: 0x1532,
                    ProductId: 0x0F43,
                    UsagePage: 0x000C,
                    Usage: 0x0001,
                    FeatureReportLength: 91,
                    ReleaseNumber: 0x0200,
                    FeatureReportId: 5,
                    ControlReportLength: RazerReport.ReportLength,
                },
                Fan:
                {
                    OpaqueQuery: "0D/81:010500",
                    HostControlPreparation: "0D/10:000600",
                    HostDemand2200: "0D/01:01052C",
                    ExactIdentityAndTransportReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-08-rz09-0581-cooling-pad-module-discovery.json",
                            Sha256: "B3A485F413DA7A8FED175268380EAB9C43E6ECE035C87EF267CDD271F6932A0C",
                        },
                    },
                    HostControl2200PairPhysicallyValidated:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-09-rz09-0581-cooling-pad-host-control-physical-positive.json",
                            Sha256: "58908312EEBD407D3D2C647F59F8C9CFC97C06D417D0D67D76075C235FB965BC",
                        },
                    },
                    OpenBladeHandbackValidated: { Validated: false, Evidence: null },
                    FullTargetRangeValidated: { Validated: false, Evidence: null },
                    SmartCurveCadenceValidated: { Validated: false, Evidence: null },
                    DisconnectReconnectValidated: { Validated: false, Evidence: null },
                    LowPowerLifecycleValidated: { Validated: false, Evidence: null },
                    CrashRecoveryValidated: { Validated: false, Evidence: null },
                    HyperBoostInterlockValidated: { Validated: false, Evidence: null },
                    PhysicalButtonOwnershipPolicyValidated: { Validated: false, Evidence: null },
                },
                Lighting:
                {
                    FrameCommand: "0F/03",
                    FramePayloadLength: CoolingPadLightingProtocol.FramePayloadLength,
                    LogicalSlotCount: CoolingPadLightingFrame.SlotCount,
                    UniformKnownAnswerColor: "123456",
                    UniformKnownAnswerReportSha256: KnownAnswerReportSha256,
                    BrightnessQuery: "0F/84:010500",
                    BrightnessSetPrefix: "0F/04:0100",
                    ExactIdentityAndGeometryReviewed:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-08-rz09-0581-cooling-pad-module-discovery.json",
                            Sha256: "B3A485F413DA7A8FED175268380EAB9C43E6ECE035C87EF267CDD271F6932A0C",
                        },
                    },
                    WholeStripProjectionResolved:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/decoded/rz09-0581-pid-02e0-bios-4.00-cooling-pad-protocol-observations.json",
                            Sha256: "6A3825D706314889948F5BD5E9DBDCE7A27E86A10E91B631952ECEE14CDB71FE",
                        },
                    },
                    DisplayOffOracleObserved:
                    {
                        Validated: true,
                        Evidence:
                        {
                            Artifact: "captures/annotations/2026-08-09-rz09-0581-cooling-pad-display-off.json",
                            Sha256: "AF3EB444747E10A789B5DE0EDD2BC2A63B36A13C427D932EEDAAF386CE8F5F46",
                        },
                    },
                    OpenBladeReversibleFrameLifecycleValidated: { Validated: false, Evidence: null },
                    FrameCadenceValidated: { Validated: false, Evidence: null },
                    DisconnectReconnectValidated: { Validated: false, Evidence: null },
                    SleepResumeValidated: { Validated: false, Evidence: null },
                    ControllerHandoffValidated: { Validated: false, Evidence: null },
                },
            })
        {
            throw Invalid("The embedded cooling-pad admission manifest does not match the exact reviewed semantic contract.");
        }

        if (!ReviewedAdmissionGate.AllWellFormed(
            manifest.Fan.ExactIdentityAndTransportReviewed,
            manifest.Fan.HostControl2200PairPhysicallyValidated,
            manifest.Fan.OpenBladeHandbackValidated,
            manifest.Fan.FullTargetRangeValidated,
            manifest.Fan.SmartCurveCadenceValidated,
            manifest.Fan.DisconnectReconnectValidated,
            manifest.Fan.LowPowerLifecycleValidated,
            manifest.Fan.CrashRecoveryValidated,
            manifest.Fan.HyperBoostInterlockValidated,
            manifest.Fan.PhysicalButtonOwnershipPolicyValidated,
            manifest.Lighting.ExactIdentityAndGeometryReviewed,
            manifest.Lighting.WholeStripProjectionResolved,
            manifest.Lighting.DisplayOffOracleObserved,
            manifest.Lighting.OpenBladeReversibleFrameLifecycleValidated,
            manifest.Lighting.FrameCadenceValidated,
            manifest.Lighting.DisconnectReconnectValidated,
            manifest.Lighting.SleepResumeValidated,
            manifest.Lighting.ControllerHandoffValidated))
        {
            throw Invalid(
                "Every cooling-pad admission gate must bind exact reviewed evidence.");
        }

        var uniform = new CoolingPadLightingFrame(
            Enumerable.Repeat(new RgbColor(0x12, 0x34, 0x56),
                CoolingPadLightingFrame.SlotCount).ToArray());
        string encodedHash = Convert.ToHexString(SHA256.HashData(
            CoolingPadLightingProtocol.CreateFrame(0x5A, uniform).Encode()));
        if (!string.Equals(
            encodedHash,
            manifest.Lighting.UniformKnownAnswerReportSha256,
            StringComparison.Ordinal))
        {
            throw Invalid("The cooling-pad lighting known-answer report does not match the embedded manifest.");
        }
    }

    private static UnvalidatedCommandException Invalid(string message) => new(message);
}
