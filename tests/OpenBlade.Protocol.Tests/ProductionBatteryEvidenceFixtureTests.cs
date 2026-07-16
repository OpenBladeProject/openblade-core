using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionBatteryEvidenceFixtureTests
{
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
