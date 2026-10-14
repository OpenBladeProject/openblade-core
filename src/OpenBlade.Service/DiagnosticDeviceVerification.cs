using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed record DiagnosticDeviceVerificationAttempt(
    DateTimeOffset ObservedAt,
    string Stage,
    string Classification,
    string Reason,
    BatteryValidationProbeResult? ProbeResult,
    bool UsedRecentlyVerifiedDevice = false,
    string? ExceptionType = null,
    int? HResult = null)
{
    internal static DiagnosticDeviceVerificationAttempt FromProbe(
        BatteryValidationProbeResult result,
        bool usedRecentlyVerifiedDevice) => new(
            DateTimeOffset.UtcNow,
            result.Stage.ToString(),
            result.Classification.ToString(),
            Describe(result),
            result,
            usedRecentlyVerifiedDevice);

    private static string Describe(BatteryValidationProbeResult result) =>
        result.Classification switch
        {
            BatteryValidationProbeClassification.Ready =>
                "The exact supported laptop and control interface were verified.",
            BatteryValidationProbeClassification.ReadyWithAccessFallback =>
                "The exact control interface was verified using metadata-only access fallback.",
            BatteryValidationProbeClassification.HostIdentityBlocked =>
                "The Windows platform or exact laptop identity check failed; see hostIdentityFailure.",
            BatteryValidationProbeClassification.ConflictBlocked =>
                "A competing controller blocked device verification.",
            BatteryValidationProbeClassification.ConflictDetectionFailed =>
                "Competing-controller detection could not be completed.",
            BatteryValidationProbeClassification.EnumerationFailed =>
                "Windows HID candidate enumeration failed.",
            BatteryValidationProbeClassification.CandidateOpenFailed =>
                "A HID candidate could not be opened; see candidateOpenWin32Error.",
            BatteryValidationProbeClassification.HandleIdentityFailed =>
                "The identity of an opened HID candidate could not be read.",
            BatteryValidationProbeClassification.NoExactHandle =>
                "No HID handle matched all required identity fields; see the match counters.",
            BatteryValidationProbeClassification.AmbiguousExactHandles =>
                "More than one HID handle matched the exact device identity.",
            _ => "The device verification probe returned an unknown classification.",
        };
}

public sealed record DiagnosticDeviceVerification(
    DiagnosticDeviceVerificationAttempt? LatestAttempt,
    DiagnosticDeviceVerificationAttempt? LastFailure)
{
    public string EvidenceSource => "CachedServiceAdvisoryProbe";

    // True only when the latest probe read the laptop's BIOS manufacturer or
    // product name and it matched no admitted model. A registry read failure,
    // a missing value, a platform failure, a source mismatch or a firmware
    // block is not proof that the laptop is unsupported, so those stay false.
    [JsonIgnore]
    public bool LatestAttemptFoundUnsupportedLaptop =>
        LatestAttempt?.ProbeResult is
        {
            Classification: BatteryValidationProbeClassification.HostIdentityBlocked,
            HostIdentityFailure:
            {
                Reason: HostIdentityFailureReason.RegistryValueMismatch,
                Source: "BIOS",
                Field: "SystemManufacturer" or "SystemProductName",
            } failure,
        }
        && !NamesAdmittedModel(failure);

    // The service verifies the admitted model the BIOS names. If that first
    // read fails, it verifies the first admitted model instead, and a later
    // read can report another admitted model's name as a mismatch.
    private static bool NamesAdmittedModel(HostIdentityFailureDetail failure) =>
        BatteryValidationFactory.AdmittedServiceModels.Any(model => string.Equals(
            failure.Field == "SystemManufacturer"
                ? model.HostIdentity.Manufacturer
                : model.HostIdentity.ProductName,
            failure.ActualValue,
            StringComparison.Ordinal));

    public static DiagnosticDeviceVerification NotObserved { get; } = new(null, null);
}
