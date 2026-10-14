using System.Text.Json;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class DiagnosticDeviceVerificationTests
{
    [Theory]
    [InlineData(HostIdentityFailureReason.RegistryValueMismatch, "SystemManufacturer", true)]
    [InlineData(HostIdentityFailureReason.RegistryValueMismatch, "SystemProductName", true)]
    [InlineData(HostIdentityFailureReason.RegistryValueMismatch, "SystemSKU", false)]
    [InlineData(HostIdentityFailureReason.RegistryValueMismatch, "Version", false)]
    [InlineData(HostIdentityFailureReason.RegistryReadFailed, "SystemManufacturer", false)]
    [InlineData(HostIdentityFailureReason.RegistryReadFailed, "SystemProductName", false)]
    [InlineData(HostIdentityFailureReason.RegistryValueMissing, "SystemProductName", false)]
    [InlineData(HostIdentityFailureReason.RegistrySourcesMismatch, "SystemProductName", false)]
    [InlineData(HostIdentityFailureReason.PlatformNotSupported, "SystemProductName", false)]
    [InlineData(HostIdentityFailureReason.PlatformReadFailed, "SystemProductName", false)]
    [InlineData(HostIdentityFailureReason.FirmwareNotAdmitted, "SystemProductName", false)]
    [InlineData(HostIdentityFailureReason.IdentityReadFailed, "SystemProductName", false)]
    public void LatestAttemptFoundUnsupportedLaptop_RequiresReadIdentityMismatch(
        HostIdentityFailureReason reason,
        string field,
        bool expected)
    {
        DiagnosticDeviceVerification verification = Verification(
            Probe(
                BatteryValidationProbeClassification.HostIdentityBlocked,
                new HostIdentityFailureDetail(reason, "BIOS", field, "Expected", "Actual")));

        Assert.Equal(expected, verification.LatestAttemptFoundUnsupportedLaptop);
    }

    [Theory]
    [InlineData("BIOS", "SystemProductName", "Blade 18 - RZ09-0484", true)]
    [InlineData("BIOS", "SystemManufacturer", "Contoso", true)]
    // An admitted RZ09-0581 that fails a later check of its own identity is
    // reported by the RZ09-0528 fallback probe as a product-name mismatch.
    [InlineData("BIOS", "SystemProductName", "Blade 16 - RZ09-0581", false)]
    [InlineData("BIOS", "SystemProductName", "Blade 16 - RZ09-0528", false)]
    // A Windows SystemInformation value that drifts from the BIOS value is not
    // evidence about which laptop this is.
    [InlineData("SystemInformation", "SystemProductName", "Blade 18 - RZ09-0484", false)]
    public void LatestAttemptFoundUnsupportedLaptop_RequiresBiosValueOfNoAdmittedModel(
        string source,
        string field,
        string actual,
        bool expected)
    {
        DiagnosticDeviceVerification verification = Verification(
            Probe(
                BatteryValidationProbeClassification.HostIdentityBlocked,
                new HostIdentityFailureDetail(
                    HostIdentityFailureReason.RegistryValueMismatch,
                    source,
                    field,
                    "Expected",
                    actual)));

        Assert.Equal(expected, verification.LatestAttemptFoundUnsupportedLaptop);
    }

    [Fact]
    public void LatestAttemptFoundUnsupportedLaptop_IsFalseWithoutAMatchingProbeResult()
    {
        var mismatch = new HostIdentityFailureDetail(
            HostIdentityFailureReason.RegistryValueMismatch,
            "BIOS",
            "SystemProductName",
            "Expected",
            "Actual");

        Assert.False(DiagnosticDeviceVerification.NotObserved
            .LatestAttemptFoundUnsupportedLaptop);
        Assert.False(new DiagnosticDeviceVerification(
            new DiagnosticDeviceVerificationAttempt(
                DateTimeOffset.UtcNow, "Probe", "ProbeException", "failed", null),
            null).LatestAttemptFoundUnsupportedLaptop);
        Assert.False(Verification(
            Probe(BatteryValidationProbeClassification.Ready, null))
            .LatestAttemptFoundUnsupportedLaptop);
        Assert.False(Verification(
            Probe(BatteryValidationProbeClassification.NoExactHandle, mismatch))
            .LatestAttemptFoundUnsupportedLaptop);
        Assert.False(Verification(
            Probe(BatteryValidationProbeClassification.HostIdentityBlocked, null))
            .LatestAttemptFoundUnsupportedLaptop);
    }

    [Fact]
    public void LatestAttemptFoundUnsupportedLaptop_IgnoresAnEarlierFailure()
    {
        DiagnosticDeviceVerificationAttempt blocked = Attempt(Probe(
            BatteryValidationProbeClassification.HostIdentityBlocked,
            new HostIdentityFailureDetail(
                HostIdentityFailureReason.RegistryValueMismatch,
                "BIOS",
                "SystemProductName",
                "Expected",
                "Actual")));
        DiagnosticDeviceVerificationAttempt ready = Attempt(Probe(
            BatteryValidationProbeClassification.Ready, null));

        Assert.False(new DiagnosticDeviceVerification(ready, blocked)
            .LatestAttemptFoundUnsupportedLaptop);
    }

    [Fact]
    public void Serialization_DoesNotExposeTheDerivedPredicate()
    {
        string json = JsonSerializer.Serialize(Verification(Probe(
            BatteryValidationProbeClassification.HostIdentityBlocked,
            new HostIdentityFailureDetail(
                HostIdentityFailureReason.RegistryValueMismatch,
                "BIOS",
                "SystemProductName",
                "Expected",
                "Actual"))));

        Assert.DoesNotContain(
            "UnsupportedLaptop",
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    private static DiagnosticDeviceVerification Verification(
        BatteryValidationProbeResult probe) => new(Attempt(probe), null);

    private static DiagnosticDeviceVerificationAttempt Attempt(
        BatteryValidationProbeResult probe) => new(
            DateTimeOffset.UtcNow,
            probe.Stage.ToString(),
            probe.Classification.ToString(),
            "reason",
            probe);

    private static BatteryValidationProbeResult Probe(
        BatteryValidationProbeClassification classification,
        HostIdentityFailureDetail? failure) => new(
            BatteryValidationProbeStage.HostIdentity,
            classification,
            HostIdentityMatched: false,
            ConflictCheckPassed: false,
            EnumeratedCandidateCount: 0,
            CandidateOpenAttemptCount: 0,
            CandidateOpenSuccessCount: 0,
            CandidateOpenWin32Error: null,
            CandidateAccessFallbackCount: 0,
            IdentityReadCount: 0,
            VendorMatchCount: 0,
            ProductMatchCount: 0,
            UsagePageMatchCount: 0,
            UsageMatchCount: 0,
            FeatureLengthMatchCount: 0,
            ExactMatchCount: 0,
            HostIdentityFailure: failure);
}
