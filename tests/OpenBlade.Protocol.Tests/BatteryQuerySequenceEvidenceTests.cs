using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatteryQuerySequenceEvidenceTests
{
    private const string CaptureId = "battery-query-sequence-one-second-negative";
    private const string CaptureHash = "83D9C6BDFC6FA757FDF93E431C027F741C2F7172F1A6D3493B8BDA95CB0CCA3D";

    [Fact]
    public void Evidence_RecordsExactCaptureProvenanceAndDeviceIdentity()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        JsonElement device = root.GetProperty("device");
        JsonElement capture = root.GetProperty("captureSources")
            .EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == CaptureId);
        JsonElement evidence = root.GetProperty("batteryQuerySequenceEvidence");

        Assert.Equal("RZ09-0581", device.GetProperty("model").GetString());
        Assert.Equal("1532", device.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", device.GetProperty("productId").GetString());
        Assert.Equal("3.01", device.GetProperty("biosVersion").GetString());
        Assert.Equal("openblade-battery-query-sequence-20260714.pcap", capture.GetProperty("fileName").GetString());
        Assert.Equal(CaptureHash, capture.GetProperty("sha256").GetString());
        Assert.Equal(3585, capture.GetProperty("bytes").GetInt32());
        Assert.Equal("1784091240.288634000", capture.GetProperty("firstEpoch").GetString());
        Assert.Equal("1784091296.254691000", capture.GetProperty("lastEpoch").GetString());
        Assert.Equal(CaptureId, evidence.GetProperty("capture").GetString());
        Assert.Equal("capture-scoped-negative-diagnostic", evidence.GetProperty("scope").GetString());
    }

    [Fact]
    public void Evidence_RecordsExactTransactionsStatusesAndDecodedPayload()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement[] transactions = document.RootElement
            .GetProperty("batteryQuerySequenceEvidence")
            .GetProperty("transactions")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, transactions.Length);

        JsonElement query1 = transactions[0];
        Assert.Equal(1, query1.GetProperty("queryOrdinal").GetInt32());
        Assert.Equal(1, query1.GetProperty("transactionId").GetInt32());
        Assert.Equal(29, query1.GetProperty("setReportFrame").GetInt32());
        Assert.Equal(31, query1.GetProperty("getReportFrame").GetInt32());
        Assert.Equal(32, query1.GetProperty("responseFrame").GetInt32());
        Assert.Equal("1784091269.902250000", query1.GetProperty("requestEpoch").GetString());
        Assert.Equal("1784091269.904590000", query1.GetProperty("responseEpoch").GetString());
        Assert.Equal("02", query1.GetProperty("statusHex").GetString());
        Assert.Equal("50", query1.GetProperty("batterySettingHex").GetString());
        Assert.Equal("ProtectionOff", query1.GetProperty("decodedSetting").GetString());

        JsonElement query2 = transactions[1];
        Assert.Equal(2, query2.GetProperty("queryOrdinal").GetInt32());
        Assert.Equal(2, query2.GetProperty("transactionId").GetInt32());
        Assert.Equal(33, query2.GetProperty("setReportFrame").GetInt32());
        Assert.Equal(35, query2.GetProperty("getReportFrame").GetInt32());
        Assert.Equal(36, query2.GetProperty("responseFrame").GetInt32());
        Assert.Equal("1784091270.925382000", query2.GetProperty("requestEpoch").GetString());
        Assert.Equal("1784091270.926054000", query2.GetProperty("responseEpoch").GetString());
        Assert.Equal("01", query2.GetProperty("statusHex").GetString());
        Assert.Equal("Command Busy", query2.GetProperty("semanticStatus").GetString());
    }

    [Fact]
    public void Evidence_PreservesNegativeOneSecondResultAndBoundsNextReadOnlyInterval()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement.GetProperty("batteryQuerySequenceEvidence");
        JsonElement finalQuery = evidence.GetProperty("independentFinalQuery");
        JsonElement conclusion = evidence.GetProperty("conclusion");

        Assert.Equal(1.023132m, evidence.GetProperty("requestSpacingSeconds").GetDecimal());
        Assert.Equal(0, evidence.GetProperty("setterReport07_12Count").GetInt32());
        Assert.Equal("query-battery", finalQuery.GetProperty("command").GetString());
        Assert.True(finalQuery.GetProperty("performedAfterCaptureStopped").GetBoolean());
        Assert.Equal("ProtectionOff", finalQuery.GetProperty("setting").GetString());
        Assert.Equal("None", finalQuery.GetProperty("failure").GetString());
        Assert.True(finalQuery.GetProperty("success").GetBoolean());
        Assert.False(conclusion.GetProperty("oneSecondSameSessionSpacingSufficientInCapture").GetBoolean());
        Assert.Equal(8, conclusion.GetProperty("nextBoundedReadOnlyIntervalSeconds").GetInt32());
        Assert.Contains("this capture", conclusion.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("eight seconds", conclusion.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_KeepsRawReportsAndProductionAdmissionClosed()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        string evidence = root.GetProperty("batteryQuerySequenceEvidence").GetRawText();

        Assert.DoesNotContain("rawReport", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportBody", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(CaptureHash)));
        Assert.False(root.GetProperty("admission").GetProperty("productionHashAdded").GetBoolean());
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

        throw new FileNotFoundException($"Could not locate repository file {Path.Combine(relativeSegments)}.");
    }
}
