using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionBatteryEvidenceFixtureTests
{
    [Fact]
    public void AdapterPowerEvidence_MapsUsbCAndFullPowerWithSynapseCorroboration()
    {
        using JsonDocument document = ReadFixture(
            "rz09-0581-pid-02e0-bios-3.01-adapter-power-readback-fixture.json");
        JsonElement root = document.RootElement;

        AssertTarget(root);
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("productionWired").GetBoolean());
        JsonElement query = root.GetProperty("query");
        Assert.Equal("07", query.GetProperty("commandClass").GetString());
        Assert.Equal("8C", query.GetProperty("commandId").GetString());
        Assert.Equal("0000", query.GetProperty("requestPayloadHex").GetString());
        Assert.Equal(2, query.GetProperty("expectedResponsePayloadLength").GetInt32());

        JsonElement[] mappings = root.GetProperty("mappings").EnumerateArray().ToArray();
        Assert.Equal(["0711", "1111"], mappings.Select(mapping =>
            mapping.GetProperty("responsePayloadHex").GetString()));
        Assert.Equal([100, 280], mappings.Select(mapping =>
            mapping.GetProperty("adapterWattageLevel").GetInt32()));
        Assert.Equal(["UsbCPower100W", "FullPower280W"], mappings.Select(mapping =>
            mapping.GetProperty("classification").GetString()));

        JsonElement corroboration = root.GetProperty("synapseCorroboration");
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-17T16:05:53.296-07:00"),
            DateTimeOffset.Parse(corroboration.GetProperty("observedAtLocal").GetString()!));
        Assert.Equal(100, corroboration.GetProperty("currentAdapterWattageLevel").GetInt32());
        Assert.Equal(280, corroboration.GetProperty("recommendedAdapterWattageLevel").GetInt32());
        Assert.True(corroboration.GetProperty("lowPower").GetBoolean());
        Assert.Equal("BALANCED_MODE_AC", corroboration.GetProperty("thermalFanMode").GetString());
        Assert.Equal("Balanced", corroboration.GetProperty("visiblePerformance").GetString());
        Assert.False(root.GetProperty("rawCapturesCommitted").GetBoolean());
    }

    [Fact]
    public void UsbCAdapterClassEvidence_AdmitsOnlyReviewedBios400Levels()
    {
        using JsonDocument document = ReadFixture(
            "rz09-0581-pid-02e0-bios-4.00-usb-c-power-classes.json");
        JsonElement root = document.RootElement;

        JsonElement device = root.GetProperty("device");
        Assert.Equal("RZ09-0581", device.GetProperty("modelNumber").GetString());
        Assert.Equal("1532", device.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", device.GetProperty("productId").GetString());
        Assert.Equal("4.00", device.GetProperty("biosVersion").GetString());

        JsonElement source = root.GetProperty("sourceEvidence");
        Assert.True(source.GetProperty("readOnly").GetBoolean());
        Assert.False(source.GetProperty("rawCapturesCommitted").GetBoolean());
        Assert.Equal(
            "3D9F4A0E4651FBA9EEEC4D1ACA8FFD8ADFAF82B396620028061666D11BD7454F",
            source.GetProperty("annotationSha256").GetString());

        JsonElement responseFormat = root.GetProperty("responseFormat");
        Assert.Equal(
            "CurrentAdapterWattageLevel",
            responseFormat.GetProperty("byte0").GetString());
        Assert.Equal(
            "RecommendedAdapterWattageLevel",
            responseFormat.GetProperty("byte1").GetString());
        Assert.Equal(280, responseFormat.GetProperty("recommendedWatts").GetInt32());

        JsonElement[] mappings = root.GetProperty("mappings").EnumerateArray().ToArray();
        Assert.Equal(
            ["0211", "0411"],
            mappings.Select(mapping =>
                mapping.GetProperty("responsePayloadHex").GetString()));
        Assert.Equal(
            [45, 65],
            mappings.Select(mapping => mapping.GetProperty("currentWatts").GetInt32()));
        Assert.Equal(
            ["UsbCPower45W", "UsbCPower65W"],
            mappings.Select(mapping => mapping.GetProperty("classification").GetString()));
        Assert.Equal(
            [2, 4],
            mappings.Select(mapping =>
                mapping.GetProperty("successfulReadOnlySamples").GetInt32()));

        JsonElement[] synapseMappings = root.GetProperty("authoritativeSynapseMappings")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            ["0311", "0511", "0611"],
            synapseMappings.Select(mapping =>
                mapping.GetProperty("responsePayloadHex").GetString()));
        Assert.Equal(
            [60, 80, 90],
            synapseMappings.Select(mapping =>
                mapping.GetProperty("currentWatts").GetInt32()));
        Assert.Equal(
            ["UsbCPower60W", "UsbCPower80W", "UsbCPower90W"],
            synapseMappings.Select(mapping =>
                mapping.GetProperty("classification").GetString()));
        Assert.All(
            synapseMappings,
            mapping => Assert.False(
                mapping.GetProperty("physicalSampleCaptured").GetBoolean()));

        JsonElement admission = root.GetProperty("productionAdmission");
        Assert.Equal("RZ09-0581", admission.GetProperty("modelNumber").GetString());
        Assert.Equal("4.00", admission.GetProperty("biosVersion").GetString());
        Assert.True(admission.GetProperty("otherValuesFailClosed").GetBoolean());
        Assert.True(admission.GetProperty("notGeneralizedToOtherModels").GetBoolean());
    }

    [Fact]
    public void TemporaryFullChargeReadEvidence_ContainsFiveValidatedInactiveReplies()
    {
        using JsonDocument document = ReadFixture(
            "rz09-0581-pid-02e0-bios-3.01-temporary-full-charge-read-fixture.json");
        JsonElement root = document.RootElement;

        AssertTarget(root);
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("productionWired").GetBoolean());
        JsonElement validation = root.GetProperty("validation");
        Assert.Equal(5, validation.GetProperty("successfulReplies").GetInt32());
        Assert.Equal(0, validation.GetProperty("forbiddenEnableWrites").GetInt32());
        Assert.Equal(0, validation.GetProperty("forbiddenBatterySetters").GetInt32());

        JsonElement[] exchanges = root.GetProperty("exchanges").EnumerateArray().ToArray();
        Assert.Equal(5, exchanges.Length);
        foreach (JsonElement exchange in exchanges)
        {
            AssertExchange(exchange, [0x00]);
        }
    }

    [Fact]
    public void ProtectionOffEvidence_ValidatesApplyReadbackAndLimit80Restoration()
    {
        using JsonDocument document = ReadFixture(
            "rz09-0581-pid-02e0-bios-3.01-protection-off-validation-fixture.json");
        JsonElement root = document.RootElement;

        AssertTarget(root);
        Assert.False(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("productionWired").GetBoolean());
        JsonElement validation = root.GetProperty("validation");
        Assert.Equal("Limit80", validation.GetProperty("baselineProtection").GetString());
        Assert.Equal("ProtectionOff", validation.GetProperty("candidateProtection").GetString());
        Assert.Equal("Limit80", validation.GetProperty("restorationProtection").GetString());
        Assert.True(validation.GetProperty("applied").GetBoolean());
        Assert.True(validation.GetProperty("readBack").GetBoolean());
        Assert.True(validation.GetProperty("restored").GetBoolean());
        Assert.False(validation.GetProperty("baselineTemporaryFullCharge").GetBoolean());
        Assert.False(validation.GetProperty("candidateTemporaryFullCharge").GetBoolean());
        Assert.False(validation.GetProperty("restorationTemporaryFullCharge").GetBoolean());

        JsonElement[] exchanges = root.GetProperty("exchanges").EnumerateArray().ToArray();
        Assert.Equal(8, exchanges.Length);
        foreach (JsonElement exchange in exchanges)
        {
            byte[] expected = Convert.FromHexString(
                exchange.GetProperty("expectedResponsePayloadHex").GetString()!);
            AssertExchange(exchange, expected);
        }
    }

    [Fact]
    public void TemporaryFullChargeEvidence_ValidatesEnableReadbackCancelAndRestoration()
    {
        using JsonDocument document = ReadFixture(
            "rz09-0581-pid-02e0-bios-3.01-temporary-full-charge-validation-fixture.json");
        JsonElement root = document.RootElement;

        AssertTarget(root);
        Assert.False(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("productionWired").GetBoolean());
        JsonElement source = root.GetProperty("source");
        Assert.Equal(
            "AF7EBDCBCB1189150F4FC8E7229E6E007035DC69C30749FF9342B455D75B58D9",
            source.GetProperty("sha256").GetString());
        Assert.Equal(3415, source.GetProperty("bytes").GetInt32());
        Assert.Equal(38, source.GetProperty("frameCount").GetInt32());
        Assert.Equal(0, root.GetProperty("cliResultSource").GetProperty("exitCode").GetInt32());

        JsonElement validation = root.GetProperty("validation");
        Assert.Equal(8, validation.GetProperty("requestCount").GetInt32());
        Assert.Equal(8, validation.GetProperty("responseCount").GetInt32());
        Assert.Equal(2, validation.GetProperty("temporarySetterCount").GetInt32());
        Assert.Equal(0, validation.GetProperty("unexpected90ByteCommandCount").GetInt32());
        Assert.All(
            validation.GetProperty("setCompletionToGetMilliseconds").EnumerateArray(),
            gap => Assert.True(gap.GetDecimal() >= 20m));
        Assert.Equal(
            25.315m,
            validation.GetProperty("minimumSetCompletionToQueryMilliseconds").GetDecimal());
        Assert.True(validation.GetProperty("allEnvelopeLengths90").GetBoolean());
        Assert.True(validation.GetProperty("allTransactionsMatched").GetBoolean());
        Assert.True(validation.GetProperty("allStatusesSuccessful").GetBoolean());
        Assert.True(validation.GetProperty("allChecksumsValid").GetBoolean());
        Assert.Equal("Limit80", validation.GetProperty("baselineProtection").GetString());
        Assert.False(validation.GetProperty("baselineTemporaryFullCharge").GetBoolean());
        Assert.Equal("Limit80", validation.GetProperty("candidateProtection").GetString());
        Assert.True(validation.GetProperty("candidateTemporaryFullCharge").GetBoolean());
        Assert.Equal("Limit80", validation.GetProperty("restorationProtection").GetString());
        Assert.False(validation.GetProperty("restorationTemporaryFullCharge").GetBoolean());
        Assert.True(validation.GetProperty("enableAcknowledged").GetBoolean());
        Assert.True(validation.GetProperty("readBack").GetBoolean());
        Assert.True(validation.GetProperty("cancelAcknowledged").GetBoolean());
        Assert.True(validation.GetProperty("restored").GetBoolean());
        Assert.True(validation.GetProperty("strictAcceptanceConditionsPassed").GetBoolean());

        JsonElement[] exchanges = root.GetProperty("exchanges").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "baseline-protection-query",
                "baseline-temporary-query",
                "enable-temporary-full-charge",
                "candidate-protection-query",
                "candidate-temporary-query",
                "cancel-temporary-full-charge",
                "restoration-protection-query",
                "restoration-temporary-query",
            ],
            exchanges.Select(exchange => exchange.GetProperty("phase").GetString()));
        Assert.Equal(
            ["07/92", "07/8F", "07/0F", "07/92", "07/8F", "07/0F", "07/92", "07/8F"],
            exchanges.Select(exchange =>
            {
                byte[] request = Convert.FromHexString(
                    exchange.GetProperty("requestEnvelopeHex").GetString()!);
                return $"{request[6]:X2}/{request[7]:X2}";
            }));
        Assert.Equal(
            ["D0", "00", "04", "D0", "04", "00", "D0", "00"],
            exchanges.Select(exchange => exchange.GetProperty("expectedResponsePayloadHex").GetString()));

        foreach (JsonElement exchange in exchanges)
        {
            byte[] expected = Convert.FromHexString(
                exchange.GetProperty("expectedResponsePayloadHex").GetString()!);
            AssertExchange(exchange, expected);
        }

        JsonElement finalState = root.GetProperty("independentFinalQuery");
        Assert.Equal("Limit80", finalState.GetProperty("protection").GetString());
        Assert.False(finalState.GetProperty("temporaryFullCharge").GetBoolean());
        Assert.True(finalState.GetProperty("protectionSuccess").GetBoolean());
        Assert.True(finalState.GetProperty("temporaryFullChargeSuccess").GetBoolean());
    }

    [Fact]
    public void BatteryLimitEvidence_ValidatesEveryApplyReadbackAndLimit80Restoration()
    {
        using JsonDocument document = ReadFixture(
            "rz09-0581-pid-02e0-bios-3.01-battery-limits-validation-fixture.json");
        JsonElement root = document.RootElement;

        AssertTarget(root);
        Assert.True(root.GetProperty("productionWired").GetBoolean());
        JsonElement validation = root.GetProperty("validation");
        Assert.Equal(30, validation.GetProperty("requestCount").GetInt32());
        Assert.Equal(30, validation.GetProperty("responseCount").GetInt32());
        Assert.Equal(18, validation.GetProperty("protectionQueryCount").GetInt32());
        Assert.Equal(12, validation.GetProperty("batterySetterCount").GetInt32());
        Assert.Equal(0, validation.GetProperty("unexpected90ByteCommandCount").GetInt32());
        Assert.Equal(21.689m, validation.GetProperty("minimumSetCompletionToQueryMilliseconds").GetDecimal());
        Assert.True(validation.GetProperty("allEnvelopeLengths90").GetBoolean());
        Assert.True(validation.GetProperty("allTransactionsMatched").GetBoolean());
        Assert.True(validation.GetProperty("allStatusesSuccessful").GetBoolean());
        Assert.True(validation.GetProperty("allChecksumsValid").GetBoolean());
        Assert.Equal(
            ["Limit75", "Limit70", "Limit65", "Limit60", "Limit55", "Limit50"],
            validation.GetProperty("candidateOrder").EnumerateArray()
                .Select(value => value.GetString()));

        JsonElement[] cycles = root.GetProperty("cycles").EnumerateArray().ToArray();
        Assert.Equal(6, cycles.Length);
        Assert.Equal(30, cycles.Sum(cycle => cycle.GetProperty("exchanges").GetArrayLength()));
        int queryCount = 0;
        int setterCount = 0;
        foreach (JsonElement cycle in cycles)
        {
            Assert.Equal("Limit80", cycle.GetProperty("baseline").GetString());
            Assert.Equal(cycle.GetProperty("candidate").GetString(), cycle.GetProperty("readBack").GetString());
            Assert.Equal("Limit80", cycle.GetProperty("restoredTo").GetString());
            Assert.True(cycle.GetProperty("applied").GetBoolean());
            Assert.True(cycle.GetProperty("restored").GetBoolean());
            Assert.True(cycle.GetProperty("success").GetBoolean());

            string candidatePayload = Convert.ToHexString(
                [BatteryValidationProtocol.Encode(Enum.Parse<BatteryValidationSetting>(
                    cycle.GetProperty("candidate").GetString()!))]);
            JsonElement[] exchanges = cycle.GetProperty("exchanges").EnumerateArray().ToArray();
            Assert.Equal(
                [
                    "baseline-protection-query",
                    $"apply-limit-{cycle.GetProperty("candidatePercent").GetInt32()}",
                    "candidate-protection-query",
                    "restore-limit-80",
                    "restoration-protection-query",
                ],
                exchanges.Select(exchange => exchange.GetProperty("phase").GetString()));
            Assert.Equal(
                ["07/92", "07/12", "07/92", "07/12", "07/92"],
                exchanges.Select(exchange => exchange.GetProperty("command").GetString()));
            Assert.Equal(
                ["D0", candidatePayload, candidatePayload, "D0", "D0"],
                exchanges.Select(exchange => exchange.GetProperty("expectedResponsePayloadHex").GetString()));

            foreach (JsonElement exchange in exchanges)
            {
                switch (exchange.GetProperty("command").GetString())
                {
                    case "07/92":
                        queryCount++;
                        break;
                    case "07/12":
                        setterCount++;
                        break;
                    default:
                        Assert.Fail("The battery validation fixture contains an unexpected command.");
                        break;
                }

                byte[] expected = Convert.FromHexString(
                    exchange.GetProperty("expectedResponsePayloadHex").GetString()!);
                AssertExchange(exchange, expected);
            }
        }

        Assert.Equal(validation.GetProperty("protectionQueryCount").GetInt32(), queryCount);
        Assert.Equal(validation.GetProperty("batterySetterCount").GetInt32(), setterCount);
    }

    private static void AssertExchange(JsonElement exchange, byte[] expectedPayload)
    {
        byte[] requestEnvelope = Convert.FromHexString(
            exchange.GetProperty("requestEnvelopeHex").GetString()!);
        byte[] responseEnvelope = Convert.FromHexString(
            exchange.GetProperty("responseEnvelopeHex").GetString()!);
        Assert.Equal(RazerReport.ReportLength, requestEnvelope.Length);
        Assert.Equal(RazerReport.ReportLength, responseEnvelope.Length);

        int payloadLength = requestEnvelope[5];
        RazerReport request = RazerReport.Create(
            requestEnvelope[1],
            requestEnvelope[6],
            requestEnvelope[7],
            requestEnvelope.AsSpan(8, payloadLength));
        Assert.Equal(requestEnvelope, request.Encode());
        Assert.Equal(
            expectedPayload,
            request.ValidateResponse(responseEnvelope, expectedPayload.Length).ToArray());
    }

    private static void AssertTarget(JsonElement root)
    {
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        JsonElement device = root.GetProperty("device");
        Assert.Equal("RZ09-0581", device.GetProperty("model").GetString());
        Assert.Equal("1532", device.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", device.GetProperty("productId").GetString());
        Assert.Equal("3.01", device.GetProperty("biosVersion").GetString());
    }

    private static JsonDocument ReadFixture(string fileName) => JsonDocument.Parse(
        File.ReadAllText(RepositoryFile(Path.Combine("captures", "decoded", fileName))));

    private static string RepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the OpenBlade repository root.");
    }
}
