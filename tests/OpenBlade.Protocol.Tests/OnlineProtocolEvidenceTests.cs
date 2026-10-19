using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class OnlineProtocolEvidenceTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedCaptureHashes =
        new Dictionary<string, string>
        {
            ["performance-silent-online"] = "565FF0CE7B3C2D3F75D195DF3A92573178FE4F8A69F4F5224C0B8D8DE6D9602D",
            ["performance-performance-online"] = "6F8FB3A9E4B4396FD7F0C7580E183830B8CB18A8090F7751EE4DDC033E30D58F",
            ["performance-balanced-online"] = "5EC21531D40DC1E96A7716E5E56E0DC224ABD5AFE0AD42FE7CCEEF0B0AB97201",
            ["fan-maximum-online"] = "99789A0D36A281CCA04E23F2D6F834A772DA0116E9150B0B3B9E8C1A6E643D07",
            ["fan-auto-online"] = "A0A26EB5F8720F373A7B8C23377CCC0C445FC1665E203E5296620F31B9BA1169",
        };

    [Fact]
    public void Evidence_RecordsExactOnlineCaptureHashes()
    {
        using JsonDocument document = LoadEvidence();
        Dictionary<string, string> actual = document.RootElement
            .GetProperty("captureSources")
            .EnumerateArray()
            .Where(source => ExpectedCaptureHashes.ContainsKey(source.GetProperty("id").GetString()!))
            .ToDictionary(
                source => source.GetProperty("id").GetString()!,
                source => source.GetProperty("sha256").GetString()!);

        Assert.Equal(ExpectedCaptureHashes, actual);
    }

    [Theory]
    [InlineData("performance-balanced", "01010000", "01020000", 125, 129, 128, 134)]
    [InlineData("performance-performance", "01010200", "01020200", 179, 183, 182, 186)]
    [InlineData("performance-silent", "01010500", "01020500", 271, 275, 274, 278)]
    public void Evidence_RecordsDecodedOnlinePerformanceMappings(
        string actionName,
        string laneOnePayload,
        string laneTwoPayload,
        int requestFrameOne,
        int requestFrameTwo,
        int responseFrameOne,
        int responseFrameTwo)
    {
        using JsonDocument document = LoadEvidence();
        JsonElement action = FindAction(document.RootElement, actionName);
        JsonElement provenance = document.RootElement.GetProperty("frameProvenance").GetProperty(actionName);

        Assert.Equal([laneOnePayload, laneTwoPayload], ModePayloads(action));
        Assert.True(action.GetProperty("gates").GetProperty("captured").GetBoolean());
        Assert.True(action.GetProperty("gates").GetProperty("decoded").GetBoolean());
        Assert.False(action.GetProperty("gates").GetProperty("applied").GetBoolean());
        Assert.False(action.GetProperty("gates").GetProperty("readBack").GetBoolean());
        Assert.False(action.GetProperty("gates").GetProperty("restored").GetBoolean());
        Assert.Equal([requestFrameOne, requestFrameTwo], Frames(provenance, "setFrames"));
        Assert.Equal([responseFrameOne, responseFrameTwo], Frames(provenance, "getResponseFrames"));
    }

    [Fact]
    public void Evidence_RecordsMaximumFanModeAndTargetWithoutClaimingReadback()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement action = FindAction(document.RootElement, "fan-maximum-5400");
        JsonElement evidence = action.GetProperty("onlineEvidence");
        JsonElement provenance = document.RootElement.GetProperty("frameProvenance").GetProperty("fan-maximum-5400");

        Assert.Equal(["01010001", "01020001"], ModePayloads(action));
        Assert.Equal(["010136", "010236"], TargetPayloads(action));
        Assert.Equal([379, 383], Frames(provenance, "modeSetFrames"));
        Assert.Equal([382, 387], Frames(provenance, "modeResponseFrames"));
        Assert.Equal([395, 400], Frames(provenance, "targetSetFrames"));
        Assert.Equal([398, 404], Frames(provenance, "targetResponseFrames"));
        Assert.Equal(54, evidence.GetProperty("targetHundredsRpm").GetInt32());
        Assert.Equal(5400, evidence.GetProperty("visuallyConfirmedRpm").GetInt32());
        Assert.False(evidence.GetProperty("independentModeReadBack").GetBoolean());
        Assert.Equal("adapter-wattage-level-readback", evidence.GetProperty("stateQuery07_8C").GetString());
        Assert.Equal("live-telemetry-not-target-setter-or-mode-readback", evidence.GetProperty("fanTelemetry0D_88").GetString());
    }

    [Fact]
    public void Evidence_RecordsFanAutoDifferentialWithoutTargetOrModeReadbackClaim()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement action = FindAction(document.RootElement, "fan-auto");
        JsonElement evidence = action.GetProperty("onlineEvidence");
        JsonElement provenance = document.RootElement.GetProperty("frameProvenance").GetProperty("fan-auto");

        Assert.Equal(["01010000", "01020000"], ModePayloads(action));
        Assert.Empty(TargetPayloads(action));
        Assert.Equal([77, 83], Frames(provenance, "modeSetFrames"));
        Assert.Equal([82, 86], Frames(provenance, "modeResponseFrames"));
        Assert.Equal([73, 76], Frames(provenance, "unresolvedPreludeFrames"));
        Assert.Equal([87, 90, 91, 94], Frames(provenance, "telemetryFrames"));
        Assert.Equal(5300, evidence.GetProperty("immediateTelemetryRpm").GetInt32());
        Assert.False(evidence.GetProperty("independentModeReadBack").GetBoolean());
        Assert.Equal("adapter-wattage-level-readback", evidence.GetProperty("stateQuery07_8C").GetString());
        Assert.True(action.GetProperty("gates").GetProperty("decoded").GetBoolean());
        Assert.False(action.GetProperty("gates").GetProperty("readBack").GetBoolean());
    }

    [Fact]
    public void Evidence_KeepsOnlineCapturesOutOfProductionAdmission()
    {
        using JsonDocument document = LoadEvidence();

        foreach (string hash in ExpectedCaptureHashes.Values)
        {
            Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(hash)));
        }
    }

    [Fact]
    public void Annotation_IsSanitizedAndRecordsOnlineRestoration()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(AnnotationPath()));
        JsonElement root = document.RootElement;

        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.True(root.GetProperty("condition").GetProperty("acOnline").GetBoolean());
        Assert.False(root.GetProperty("rawCapturesCommitted").GetBoolean());
        Assert.Equal("Balanced + Auto", root.GetProperty("finalRestoration").GetProperty("state").GetString());
        Assert.True(root.GetProperty("finalRestoration").GetProperty("visuallyConfirmed").GetBoolean());
        Assert.False(root.TryGetProperty("usbPcapRoot", out _));
        Assert.False(root.TryGetProperty("usbDeviceAddress", out _));
    }

    private static JsonDocument LoadEvidence() => JsonDocument.Parse(File.ReadAllText(EvidencePath()));

    private static string EvidencePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-evidence.json");

    private static string AnnotationPath() => RepositoryFile(
        "evidence", "legacy-captures",
        "annotations",
        "2026-07-14-online-performance-fan-session.json");

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

    private static JsonElement FindAction(JsonElement root, string actionName) => root
        .GetProperty("actions")
        .EnumerateArray()
        .Single(action => action.GetProperty("action").GetString() == actionName);

    private static string[] ModePayloads(JsonElement action) => Payloads(action, "02");

    private static string[] TargetPayloads(JsonElement action) => Payloads(action, "01");

    private static string[] Payloads(JsonElement action, string commandId) => action
        .GetProperty("commands")
        .EnumerateArray()
        .Where(command => command.GetProperty("class").GetString() == "0D")
        .Where(command => command.GetProperty("id").GetString() == commandId)
        .Select(command => command.GetProperty("payloadHex").GetString()!)
        .ToArray();

    private static int[] Frames(JsonElement provenance, string propertyName) => provenance
        .GetProperty(propertyName)
        .EnumerateArray()
        .Select(frame => frame.GetInt32())
        .ToArray();
}
