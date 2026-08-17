using System.Text.Json;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PowerSourceMatrixEvidenceTests
{
    private static readonly string[] ExpectedCaptureHashes =
    [
        "504C5F3F3E71B0A3D6B6AC4AA51528A9CB6CCFB68BB2F1748FD0C4852AD00CFD",
        "DF972057FF5780B3BF740FF7598DFC3BF95F3DD57BF82CB6C500A7313E2735E5",
        "E75B936026D850A23429EA0A466B8657C3714B00EAC4E0F214D462820E2C5094",
        "10240E82BC3DB201F793581B94B5C2F031103F011DF6225F6482A19FF7EA7501",
        "76882F8F43F75DD66DF19F0FA6E09DB4D5871F712F8D5C59F57FC6C898B3BAAF",
        "8074CBDF2ED3BF90E7F5C6F60FF20A03F1BCC41FFFE37B8E22AEEF20E40ECF5D",
        "0807E5281A65C5B0EC773B3ADE767E0C6E88C9DF896258E0FE065BA9D7704101",
        "3F9B08C12DE4049452B8B12FF8FB2169DAC25591D376A9BA93572769504B39AD",
        "7AAB57DABA5C2E056364257EA1F5AD98BAE9C0FE453645D6AD3158F2AB59884F",
    ];

    [Fact]
    public void HistoricalEvidence_RecordsCompleteExactDeviceTransitionMatrix()
    {
        using JsonDocument document = LoadHistoricalEvidence();
        JsonElement root = document.RootElement;
        JsonElement sessions = root.GetProperty("sessions");

        Assert.Equal("RZ09-0528", root.GetProperty("device").GetProperty("modelNumber").GetString());
        Assert.Equal("02C6", root.GetProperty("device").GetProperty("productIdHex").GetString());
        Assert.Equal("CapturedNotAdmitted", root.GetProperty("status").GetString());
        Assert.Equal(9, sessions.GetArrayLength());
        Assert.Equal(
            ExpectedCaptureHashes,
            sessions.EnumerateArray()
                .Select(session => session.GetProperty("captureSha256").GetString()!)
                .ToArray());
        Assert.Single(
            sessions.EnumerateArray(),
            session => session.GetProperty("transition").GetString() == "UsbCStableBaseline");
        Assert.Single(
            sessions.EnumerateArray(),
            session => session.GetProperty("transition").GetString() == "BatteryStableBaseline");
    }

    [Fact]
    public void Evidence_RecordsExactOutgoingQueryAndSourceSpecificSetterPairs()
    {
        using JsonDocument document = LoadHistoricalEvidence();
        JsonElement transport = document.RootElement.GetProperty("transport");

        Assert.Equal("07/8C:0000", transport.GetProperty("adapterPowerQuery").GetString());
        Assert.Equal(
            ["0D/02:01010300", "0D/02:01020300"],
            Strings(transport.GetProperty("batterySaverAutomatic")));
        Assert.Equal(
            ["0D/02:01010000", "0D/02:01020000"],
            Strings(transport.GetProperty("balancedAutomatic")));

        JsonElement batteryToUsbC = FindSession(document.RootElement, "BatteryToUsbC");
        Assert.Equal(3, batteryToUsbC.GetProperty("adapterPowerQueryCount").GetInt32());
        Assert.Equal(0, batteryToUsbC.GetProperty("batterySaverPairCount").GetInt32());
        Assert.Equal(1, batteryToUsbC.GetProperty("balancedPairCount").GetInt32());

        JsonElement usbCToBattery = FindSession(document.RootElement, "UsbCToBattery");
        Assert.Equal(2, usbCToBattery.GetProperty("adapterPowerQueryCount").GetInt32());
        Assert.Equal(1, usbCToBattery.GetProperty("batterySaverPairCount").GetInt32());
        Assert.Equal(0, usbCToBattery.GetProperty("balancedPairCount").GetInt32());
    }

    [Fact]
    public void CurrentEvidence_AdmitsExactAdapterAndBatteryReadback()
    {
        using JsonDocument historicalDocument = LoadHistoricalEvidence();
        JsonElement historical = historicalDocument.RootElement;
        JsonElement visible = historical.GetProperty("visibleCapabilities");
        JsonElement negative = historical.GetProperty("negativeEvidence");

        Assert.False(visible.GetProperty("usbCOnly").GetProperty("performanceAvailable").GetBoolean());
        Assert.False(visible.GetProperty("usbCOnly").GetProperty("customAvailable").GetBoolean());
        Assert.True(
            visible.GetProperty("usbCOnly").GetProperty("inadequatePowerWarningVisible").GetBoolean());
        Assert.False(visible.GetProperty("battery").GetProperty("fixedFanAvailable").GetBoolean());
        Assert.True(visible.GetProperty("barrelAc").GetProperty("customAvailable").GetBoolean());

        Assert.False(negative.GetProperty("exactUsbCPowerClassResponseCaptured").GetBoolean());
        Assert.False(negative.GetProperty("independentThermalReadbackCaptured").GetBoolean());
        Assert.False(negative.GetProperty("openBladeTypedApplyPerformed").GetBoolean());
        Assert.False(negative.GetProperty("productionWired").GetBoolean());

        using JsonDocument currentDocument = LoadCurrentEvidence();
        JsonElement power = currentDocument.RootElement.GetProperty("power");
        Assert.StartsWith(
            "ProductionAdmitted: three identical exact-device 07/8C reads returned 0711",
            power.GetProperty("usbC").GetString());
        Assert.StartsWith(
            "ProductionAdmitted: three identical exact-device 07/8C reads returned 0011",
            power.GetProperty("battery").GetString());
    }

    private static JsonDocument LoadHistoricalEvidence() => JsonDocument.Parse(
        File.ReadAllText(RepositoryFile(
            "captures",
            "decoded",
            "rz09-0528-pid-02c6-bios-2.02-power-source-matrix.json")));

    private static JsonDocument LoadCurrentEvidence() => JsonDocument.Parse(
        File.ReadAllText(RepositoryFile(
            "captures",
            "decoded",
            "rz09-0528-pid-02c6-bios-2.02-evidence.json")));

    private static JsonElement FindSession(JsonElement root, string transition) => root
        .GetProperty("sessions")
        .EnumerateArray()
        .Single(session => session.GetProperty("transition").GetString() == transition);

    private static string[] Strings(JsonElement values) => values
        .EnumerateArray()
        .Select(value => value.GetString()!)
        .ToArray();

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
