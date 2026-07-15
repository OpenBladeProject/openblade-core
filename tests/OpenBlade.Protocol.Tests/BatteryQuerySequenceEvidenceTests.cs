using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatteryQuerySequenceEvidenceTests
{
    private const string CaptureId = "battery-query-sequence-one-second-negative";
    private const string CaptureHash = "83D9C6BDFC6FA757FDF93E431C027F741C2F7172F1A6D3493B8BDA95CB0CCA3D";
    private const string EightSecondCaptureId = "battery-query-sequence-eight-second-negative";
    private const string EightSecondCaptureHash = "23AA9BFD7636C000B7AE5043C7A00C8563025635B6B09D4173D7AEE9774149E8";
    private const string SettledCaptureId = "battery-query-sequence-settled-success";
    private const string SettledCaptureHash = "EEECED460B5B4267988A69D05FABE466EED47B2582520A6C67BB18FDA7B2AFBA";

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
    public void Evidence_RecordsNoSettleBusyAndNextIntraExchangeDiscriminator()
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
        Assert.True(conclusion.GetProperty("secondQueryReturnedBusyWithoutConfiguredSetToGetSettle").GetBoolean());
        Assert.Equal(10, conclusion.GetProperty("nextDiscriminatorSetToGetSettleMilliseconds").GetInt32());
        Assert.Contains("this capture", conclusion.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10 ms SET-completion-to-GET settle", conclusion.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cooldown", conclusion.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(conclusion.TryGetProperty("oneSecondSameSessionSpacingSufficientInCapture", out _));
        Assert.False(conclusion.TryGetProperty("nextBoundedReadOnlyIntervalSeconds", out _));
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

    [Fact]
    public void EightSecondEvidence_RecordsExactCaptureProvenance()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        JsonElement capture = root.GetProperty("captureSources")
            .EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == EightSecondCaptureId);
        JsonElement evidence = root.GetProperty("batteryQuerySequenceEightSecondEvidence");

        Assert.Equal("openblade-battery-query-sequence-8s-20260714.pcap", capture.GetProperty("fileName").GetString());
        Assert.Equal(EightSecondCaptureHash, capture.GetProperty("sha256").GetString());
        Assert.Equal(3177, capture.GetProperty("bytes").GetInt32());
        Assert.Equal("1784092168.904587000", capture.GetProperty("firstEpoch").GetString());
        Assert.Equal("1784092212.263108000", capture.GetProperty("lastEpoch").GetString());
        Assert.Equal(EightSecondCaptureId, evidence.GetProperty("capture").GetString());
        Assert.Equal("capture-scoped-negative-diagnostic", evidence.GetProperty("scope").GetString());
    }

    [Fact]
    public void EightSecondEvidence_RecordsExactTransactionsAndSequenceOutcome()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement.GetProperty("batteryQuerySequenceEightSecondEvidence");
        JsonElement[] transactions = evidence.GetProperty("transactions").EnumerateArray().ToArray();

        Assert.Equal(2, transactions.Length);

        JsonElement query1 = transactions[0];
        Assert.Equal(1, query1.GetProperty("queryOrdinal").GetInt32());
        Assert.Equal(1, query1.GetProperty("transactionId").GetInt32());
        Assert.Equal(23, query1.GetProperty("setReportFrame").GetInt32());
        Assert.Equal(25, query1.GetProperty("getReportFrame").GetInt32());
        Assert.Equal(26, query1.GetProperty("responseFrame").GetInt32());
        Assert.Equal("1784092198.585862", query1.GetProperty("requestEpoch").GetString());
        Assert.Equal("1784092198.587841", query1.GetProperty("responseEpoch").GetString());
        Assert.Equal("02", query1.GetProperty("statusHex").GetString());
        Assert.Equal("50", query1.GetProperty("batterySettingHex").GetString());
        Assert.Equal("ProtectionOff", query1.GetProperty("decodedSetting").GetString());

        JsonElement query2 = transactions[1];
        Assert.Equal(2, query2.GetProperty("queryOrdinal").GetInt32());
        Assert.Equal(2, query2.GetProperty("transactionId").GetInt32());
        Assert.Equal(40, query2.GetProperty("setReportFrame").GetInt32());
        Assert.Equal(51, query2.GetProperty("getReportFrame").GetInt32());
        Assert.Equal(52, query2.GetProperty("responseFrame").GetInt32());
        Assert.Equal("1784092206.694461", query2.GetProperty("requestEpoch").GetString());
        Assert.Equal("1784092206.695602", query2.GetProperty("responseEpoch").GetString());
        Assert.Equal("01", query2.GetProperty("statusHex").GetString());
        Assert.Equal("Command Busy", query2.GetProperty("semanticStatus").GetString());
        Assert.False(query2.TryGetProperty("decodedSetting", out _));

        Assert.Equal(8.108599m, evidence.GetProperty("requestSpacingSeconds").GetDecimal());
        Assert.Equal(2, evidence.GetProperty("completedCount").GetInt32());
        Assert.False(evidence.GetProperty("success").GetBoolean());
        Assert.Equal([3, 4, 5], evidence.GetProperty("queryOrdinalsNotAttempted")
            .EnumerateArray()
            .Select(value => value.GetInt32()));
        Assert.Equal(0, evidence.GetProperty("setterReport07_12Count").GetInt32());
    }

    [Fact]
    public void EightSecondEvidence_RecordsFinalQueryAndCaptureScopedConfound()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement.GetProperty("batteryQuerySequenceEightSecondEvidence");
        JsonElement finalQuery = evidence.GetProperty("independentFinalQuery");
        JsonElement confound = evidence.GetProperty("selectiveSuspendResumeConfound");
        JsonElement conclusion = evidence.GetProperty("conclusion");

        Assert.Equal("query-battery", finalQuery.GetProperty("command").GetString());
        Assert.True(finalQuery.GetProperty("performedAfterCaptureStopped").GetBoolean());
        Assert.Equal("ProtectionOff", finalQuery.GetProperty("setting").GetString());
        Assert.Equal("None", finalQuery.GetProperty("failure").GetString());
        Assert.True(finalQuery.GetProperty("success").GetBoolean());
        Assert.True(confound.GetProperty("observed").GetBoolean());
        Assert.Equal("unresolved", confound.GetProperty("effectOnResult").GetString());
        Assert.False(conclusion.GetProperty("eightSecondsPreventedBusyInCapture").GetBoolean());
        string reason = conclusion.GetProperty("reason").GetString()!;
        Assert.Contains("eight seconds did not prevent Busy in this capture", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selective-suspend/resume", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cooldown", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EightSecondEvidence_KeepsRawReportsAndProductionAdmissionClosed()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        string evidence = root.GetProperty("batteryQuerySequenceEightSecondEvidence").GetRawText();

        Assert.DoesNotContain("rawReport", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportBody", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("annotation", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trustedFixtureHash", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(EightSecondCaptureHash)));
        Assert.False(root.GetProperty("admission").GetProperty("productionHashAdded").GetBoolean());
    }

    [Fact]
    public void SettledEvidence_RecordsExactProvenanceIdentityAndScope()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        JsonElement capture = root.GetProperty("captureSources")
            .EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == SettledCaptureId);
        JsonElement evidence = root.GetProperty("batteryQuerySequenceSettledEvidence");
        JsonElement identity = evidence.GetProperty("captureIdentity");

        Assert.Equal("openblade-battery-query-sequence-settled-20260714.pcap", capture.GetProperty("fileName").GetString());
        Assert.Equal(SettledCaptureHash, capture.GetProperty("sha256").GetString());
        Assert.Equal(3656, capture.GetProperty("bytes").GetInt32());
        Assert.Equal(56, capture.GetProperty("frameCount").GetInt32());
        Assert.Equal("1784094542.297520000", capture.GetProperty("firstEpoch").GetString());
        Assert.Equal("1784094577.836499000", capture.GetProperty("lastEpoch").GetString());
        Assert.Equal(SettledCaptureId, evidence.GetProperty("capture").GetString());
        Assert.Equal("capture-scoped-read-only-success", evidence.GetProperty("scope").GetString());
        Assert.Equal(3, identity.GetProperty("rootBus").GetInt32());
        Assert.Equal(2, identity.GetProperty("deviceAddress").GetInt32());
        Assert.Equal("1532", identity.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", identity.GetProperty("productId").GetString());
        Assert.Equal(2, identity.GetProperty("featureReportInterface").GetInt32());
    }

    [Fact]
    public void SettledEvidence_RecordsFiveExactSuccessfulTransactions()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement[] transactions = document.RootElement
            .GetProperty("batteryQuerySequenceSettledEvidence")
            .GetProperty("transactions")
            .EnumerateArray()
            .ToArray();
        var expected = new[]
        {
            (1, 1, 31, "1784094567.652130000", 32, "1784094567.652613000", 33, "1784094567.670339000", 17.726m, 34, "1784094567.670693000", (decimal?)null),
            (2, 2, 35, "1784094568.690617000", 36, "1784094568.690994000", 37, "1784094568.714859000", 23.865m, 38, "1784094568.715491000", 1.038487m),
            (3, 3, 39, "1784094569.734583000", 40, "1784094569.734892000", 41, "1784094569.752903000", 18.011m, 42, "1784094569.753310000", 1.043966m),
            (4, 4, 43, "1784094570.766632000", 44, "1784094570.767071000", 45, "1784094570.785589000", 18.518m, 46, "1784094570.786020000", 1.032049m),
            (5, 5, 47, "1784094571.808097000", 48, "1784094571.808647000", 49, "1784094571.833650000", 25.003m, 50, "1784094571.834171000", 1.041465m),
        };

        Assert.Equal(expected.Length, transactions.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            var value = expected[index];
            JsonElement transaction = transactions[index];
            Assert.Equal(value.Item1, transaction.GetProperty("queryOrdinal").GetInt32());
            Assert.Equal(value.Item2, transaction.GetProperty("transactionId").GetInt32());
            Assert.Equal(value.Item3, transaction.GetProperty("setReportFrame").GetInt32());
            Assert.Equal(value.Item4, transaction.GetProperty("setReportEpoch").GetString());
            Assert.Equal(value.Item5, transaction.GetProperty("setCompletionFrame").GetInt32());
            Assert.Equal(value.Item6, transaction.GetProperty("setCompletionEpoch").GetString());
            Assert.Equal(value.Item7, transaction.GetProperty("getReportFrame").GetInt32());
            Assert.Equal(value.Item8, transaction.GetProperty("getReportEpoch").GetString());
            Assert.Equal(value.Item9, transaction.GetProperty("setCompletionToGetMilliseconds").GetDecimal());
            Assert.Equal(value.Item10, transaction.GetProperty("responseFrame").GetInt32());
            Assert.Equal(value.Item11, transaction.GetProperty("responseEpoch").GetString());
            Assert.Equal("02", transaction.GetProperty("statusHex").GetString());
            Assert.Equal("50", transaction.GetProperty("batterySettingHex").GetString());
            Assert.Equal("ProtectionOff", transaction.GetProperty("decodedSetting").GetString());
            if (value.Item12 is { } spacing)
            {
                Assert.Equal(spacing, transaction.GetProperty("requestSpacingSeconds").GetDecimal());
            }
            else
            {
                Assert.False(transaction.TryGetProperty("requestSpacingSeconds", out _));
            }
        }
    }

    [Fact]
    public void SettledEvidence_RecordsAcceptanceCliResultAndReadOnlyBoundary()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement.GetProperty("batteryQuerySequenceSettledEvidence");
        JsonElement commands = evidence.GetProperty("commandSummary");
        JsonElement cli = evidence.GetProperty("cliResult");
        JsonElement conclusion = evidence.GetProperty("conclusion");

        Assert.Equal(90, commands.GetProperty("featureReportBytes").GetInt32());
        Assert.Equal(5, commands.GetProperty("query07_92Count").GetInt32());
        Assert.Equal(0, commands.GetProperty("setter07_12Count").GetInt32());
        Assert.Equal(0, commands.GetProperty("other90ByteFeatureCommandCount").GetInt32());
        Assert.All(
            evidence.GetProperty("transactions").EnumerateArray(),
            transaction => Assert.True(
                transaction.GetProperty("setCompletionToGetMilliseconds").GetDecimal() >= 10m));
        Assert.Equal(
            Enumerable.Repeat("ProtectionOff", 5),
            cli.GetProperty("settings").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(5, cli.GetProperty("completedCount").GetInt32());
        Assert.True(cli.GetProperty("success").GetBoolean());
        Assert.Equal(0, cli.GetProperty("exitCode").GetInt32());
        Assert.True(conclusion.GetProperty("acceptanceConditionsPassedInCapture").GetBoolean());
        Assert.True(conclusion.GetProperty("settlingResolvedPriorSameSessionBusyInCapture").GetBoolean());
        Assert.False(conclusion.GetProperty("productionBatteryWriteAdmissionChanged").GetBoolean());
        Assert.False(conclusion.GetProperty("writeAuthorizedByReadOnlyEvidence").GetBoolean());
        string reason = conclusion.GetProperty("reason").GetString()!;
        Assert.Contains("this exact capture/device", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not authorize battery writes", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettledEvidence_KeepsRawReportsAndProductionAdmissionClosed()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        string evidence = root.GetProperty("batteryQuerySequenceSettledEvidence").GetRawText();

        Assert.DoesNotContain("rawReport", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportBody", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("annotation", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trustedFixtureHash", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(SettledCaptureHash)));
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
