using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatteryValidationEvidenceTests
{
    private const string SameCandidateId = "battery-validation-same-candidate-zero-write";
    private const string SameCandidateHash =
        "A4622641B5D5513472F14675897B8CADAB0561B1668854C99C9F3FF8A8E43303";
    private const string TimingNegativeId = "battery-validation-limit80-timing-negative";
    private const string TimingNegativeHash =
        "D273D68F529641BDED9370846A11B599266A53B343FD5AB18613645B5B39ABD5";
    private const string StrictSuccessId = "battery-validation-limit80-strict-success";
    private const string StrictSuccessHash =
        "BDDB65D1664FDC3331F25282F75229B38A2011E443F5670D58AE32CA6BD36B5B";

    [Fact]
    public void Evidence_RecordsExactCaptureProvenance()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement[] sources = document.RootElement.GetProperty("captureSources")
            .EnumerateArray()
            .ToArray();

        AssertCapture(
            sources,
            SameCandidateId,
            "openblade-battery-validate-off-final-20260715.pcap",
            SameCandidateHash,
            2798,
            56);
        AssertCapture(
            sources,
            TimingNegativeId,
            "openblade-battery-limit80-final-20260715.pcap",
            TimingNegativeHash,
            4658,
            76);
        AssertCapture(
            sources,
            StrictSuccessId,
            "openblade-battery-limit80-settle20-final-20260715.pcap",
            StrictSuccessHash,
            5732,
            98);
    }

    [Fact]
    public void SameCandidateEvidence_IsZeroWriteSafetyOnly()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement
            .GetProperty("batteryValidationEvidence")
            .GetProperty("sameCandidateZeroWrite");

        Assert.Equal(SameCandidateId, evidence.GetProperty("capture").GetString());
        Assert.Equal(
            "capture-scoped-zero-write-safety-success",
            evidence.GetProperty("scope").GetString());
        Assert.False(evidence.GetProperty("cli").GetProperty("applyAttempted").GetBoolean());
        Assert.Equal("SameCandidate", evidence.GetProperty("cli").GetProperty("primaryFailure").GetString());
        Assert.Equal(0, evidence.GetProperty("setter07_12Count").GetInt32());
        Assert.Equal(0, evidence.GetProperty("other90ByteFeatureCommandCount").GetInt32());
        Assert.True(evidence.GetProperty("conclusion").GetProperty("zeroWriteSafetyPassed").GetBoolean());
        Assert.False(evidence.GetProperty("conclusion").GetProperty("writeAdmissionEvidence").GetBoolean());
    }

    [Fact]
    public void TimingNegativeEvidence_DoesNotMislabelFunctionalSuccessAsStrictAcceptance()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement
            .GetProperty("batteryValidationEvidence")
            .GetProperty("limit80TimingNegative");

        Assert.Equal(TimingNegativeId, evidence.GetProperty("capture").GetString());
        Assert.True(evidence.GetProperty("functionalSequenceObserved").GetBoolean());
        Assert.True(evidence.GetProperty("commandIsolationPassed").GetBoolean());
        Assert.Equal(9.618m, evidence.GetProperty("minimumSettleMilliseconds").GetDecimal());
        Assert.False(evidence.GetProperty("timingGatePassed").GetBoolean());
        Assert.False(evidence.GetProperty("strictAcceptanceConditionsPassed").GetBoolean());
        Assert.False(evidence.GetProperty("productionBatteryWriteAdmissionChanged").GetBoolean());
    }

    [Fact]
    public void StrictSuccessEvidence_RecordsExactSequenceTimingAndRestoration()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement
            .GetProperty("batteryValidationEvidence")
            .GetProperty("limit80StrictSuccess");

        Assert.Equal(StrictSuccessId, evidence.GetProperty("capture").GetString());
        Assert.Equal(
            ["01/07/92/00", "02/07/12/D0", "03/07/92/00", "04/07/12/50", "05/07/92/00", "01/07/92/00"],
            evidence.GetProperty("requestTuples").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["02/01/07/92/50", "02/02/07/12/D0", "02/03/07/92/D0", "02/04/07/12/50", "02/05/07/92/50", "02/01/07/92/50"],
            evidence.GetProperty("responseTuples").EnumerateArray().Select(value => value.GetString()));
        Assert.All(
            evidence.GetProperty("setCompletionToGetMilliseconds").EnumerateArray(),
            gap => Assert.True(gap.GetDecimal() >= 10m));
        Assert.Equal(22.265m, evidence.GetProperty("minimumSettleMilliseconds").GetDecimal());
        Assert.Equal(0, evidence.GetProperty("other90ByteFeatureCommandCount").GetInt32());
        Assert.True(evidence.GetProperty("cli").GetProperty("applied").GetBoolean());
        Assert.True(evidence.GetProperty("cli").GetProperty("readBack").GetBoolean());
        Assert.True(evidence.GetProperty("cli").GetProperty("restored").GetBoolean());
        Assert.True(evidence.GetProperty("cli").GetProperty("success").GetBoolean());
        Assert.Equal(
            "ProtectionOff",
            evidence.GetProperty("independentFinalQuery").GetProperty("setting").GetString());
        Assert.True(evidence.GetProperty("strictAcceptanceConditionsPassed").GetBoolean());
    }

    [Fact]
    public void Evidence_PreservesHistoricalBoundaryAndRecordsCurrentBatteryAdmission()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        JsonElement boundary = root.GetProperty("batteryValidationEvidence")
            .GetProperty("historicalAdmissionBoundaryAfterLimit80Validation");
        JsonElement admission = root.GetProperty("admission");
        JsonElement[] limits = root.GetProperty("actions")
            .EnumerateArray()
            .Where(action => action.GetProperty("action").GetString() is string name
                && name.StartsWith("battery-limit-", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(7, limits.Length);
        foreach (JsonElement limit in limits)
        {
            JsonElement gates = limit.GetProperty("gates");
            Assert.True(gates.GetProperty("captured").GetBoolean());
            Assert.True(gates.GetProperty("decoded").GetBoolean());
            Assert.True(gates.GetProperty("applied").GetBoolean());
            Assert.True(gates.GetProperty("readBack").GetBoolean());
            Assert.True(gates.GetProperty("restored").GetBoolean());
        }
        Assert.False(boundary.GetProperty("productionBatteryWriteAdmissionChanged").GetBoolean());
        Assert.False(boundary.GetProperty("limit80WriteAuthorizedInProduction").GetBoolean());
        Assert.False(boundary.GetProperty("otherBatteryLimitsAuthorizedInProduction").GetBoolean());
        Assert.False(boundary.GetProperty("fullChargeOverrideAuthorizedInProduction").GetBoolean());
        Assert.True(admission.GetProperty("productionHashAdded").GetBoolean());
        Assert.Equal(
            [
                "battery-limit-50",
                "battery-limit-55",
                "battery-limit-60",
                "battery-limit-65",
                "battery-limit-70",
                "battery-limit-75",
                "battery-limit-80",
                "battery-protection-off",
                "temporary-full-charge-query",
            ],
            admission.GetProperty("authorizedActions").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(
            "temporary-full-charge-enable",
            admission.GetProperty("unauthorizedActions").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(
            "other-battery-limits",
            admission.GetProperty("unauthorizedActions").EnumerateArray().Select(value => value.GetString()));
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(SameCandidateHash)));
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(TimingNegativeHash)));
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(StrictSuccessHash)));
    }

    private static void AssertCapture(
        IEnumerable<JsonElement> sources,
        string id,
        string fileName,
        string hash,
        int bytes,
        int frames)
    {
        JsonElement capture = sources.Single(source => source.GetProperty("id").GetString() == id);
        Assert.Equal(fileName, capture.GetProperty("fileName").GetString());
        Assert.Equal(hash, capture.GetProperty("sha256").GetString());
        Assert.Equal(bytes, capture.GetProperty("bytes").GetInt32());
        Assert.Equal(frames, capture.GetProperty("frameCount").GetInt32());
    }

    private static JsonDocument LoadEvidence() => JsonDocument.Parse(File.ReadAllText(EvidencePath()));

    private static string EvidencePath() => RepositoryFile(
        "captures", "decoded", "rz09-0581-pid-02e0-bios-3.01-evidence.json");

    private static string RepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(relativeSegments)}.");
    }
}
