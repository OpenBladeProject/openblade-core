using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PerformanceFanRefreshEvidenceTests
{
    private const string CaptureId = "performance-fan-state-refresh-negative";
    private const string CaptureHash = "8D323555E7CF173E41B0E7AFB3690D395815C4430378A4DD7B229183F836C499";
    private const string FixedHydrationCaptureId = "fan-fixed-hydration-negative";
    private const string FixedHydrationCaptureHash = "2CA5ED0700028006E6EEFADEC3DA8D447A6DD645DD1F1CCE358882550C4EDA7F";
    private const string ColdHydrationCaptureId = "performance-fan-cold-hydration-negative";
    private const string ColdHydrationCaptureHash = "611336A8D54581C8FFD6565BD553320AF1A55540C2AF9D740762E1FE0597EFB4";

    [Fact]
    public void Annotation_ContainsOnlySanitizedUiCorrelationTimestamps()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(AnnotationPath()));
        JsonElement root = document.RootElement;

        Assert.Equal(["schemaVersion", "uiActions"],
            root.EnumerateObject().Select(property => property.Name).ToArray());

        JsonElement[] actions = root.GetProperty("uiActions").EnumerateArray().ToArray();
        Assert.Equal(4, actions.Length);
        Assert.All(actions, action =>
        {
            Assert.Equal(["atUtc", "observation"],
                action.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.True(DateTimeOffset.TryParse(action.GetProperty("atUtc").GetString(), out _));
        });

        string annotation = root.GetRawText();
        Assert.DoesNotContain("sha256", annotation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("frame", annotation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0D/02", annotation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0D/88", annotation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("07/8C", annotation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admission", annotation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_RecordsExactNegativeCaptureProvenance()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        JsonElement capture = root.GetProperty("captureSources")
            .EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == CaptureId);
        JsonElement evidence = root.GetProperty("performanceFanRefreshEvidence");
        JsonElement mode = evidence.GetProperty("modeAckEchoes0D_02");

        Assert.Equal("openblade-performance-fan-refresh-20260714.pcap", capture.GetProperty("fileName").GetString());
        Assert.Equal(CaptureHash, capture.GetProperty("sha256").GetString());
        Assert.Equal(235494, capture.GetProperty("bytes").GetInt32());
        Assert.Equal("1784084232.487321000", capture.GetProperty("firstEpoch").GetString());
        Assert.Equal("1784084361.799563000", capture.GetProperty("lastEpoch").GetString());
        Assert.Equal(CaptureId, evidence.GetProperty("capture").GetString());

        Assert.Equal("0D/02", mode.GetProperty("command").GetString());
        Assert.Equal(32, mode.GetProperty("requestCount").GetInt32());
        Assert.Equal(32, mode.GetProperty("replyCount").GetInt32());
        Assert.Equal(97, mode.GetProperty("firstRequestFrame").GetInt32());
        Assert.Equal(851, mode.GetProperty("lastRequestFrame").GetInt32());
        Assert.Equal(99, mode.GetProperty("firstReplyGetRequestFrame").GetInt32());
        Assert.Equal(100, mode.GetProperty("firstReplyFrame").GetInt32());
        Assert.Equal(853, mode.GetProperty("lastReplyGetRequestFrame").GetInt32());
        Assert.Equal(854, mode.GetProperty("lastReplyFrame").GetInt32());
        Assert.True(mode.GetProperty("status02AckEchoesOnly").GetBoolean());
        Assert.False(mode.GetProperty("independentStateValues").GetBoolean());
    }

    [Fact]
    public void Evidence_DistinguishesAwayAndReturnRefreshTelemetry()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement.GetProperty("performanceFanRefreshEvidence");
        JsonElement telemetry = evidence.GetProperty("fanTelemetry0D_88");

        Assert.Equal("0D/88", telemetry.GetProperty("command").GetString());
        Assert.Equal([1091, 1095], Frames(telemetry.GetProperty("firstAfterNavigateAway"), "requestFrames"));
        Assert.Equal([1094, 1100], Frames(telemetry.GetProperty("firstAfterNavigateAway"), "responseFrames"));
        Assert.Equal([1151, 1155], Frames(telemetry.GetProperty("firstAfterReturn"), "requestFrames"));
        Assert.Equal([1154, 1158], Frames(telemetry.GetProperty("firstAfterReturn"), "responseFrames"));
        Assert.Equal("fan-rpm-only", telemetry.GetProperty("semantic").GetString());
        Assert.False(telemetry.GetProperty("performanceOrFanModeReadback").GetBoolean());

        JsonElement unresolved = evidence.GetProperty("unresolved07_8C");
        Assert.Equal("07/8C", unresolved.GetProperty("command").GetString());
        Assert.Equal("1111", unresolved.GetProperty("responsePayloadHex").GetString());
        Assert.Equal("unresolved-not-associated-with-performance-page-refresh", unresolved.GetProperty("semantic").GetString());
        Assert.False(unresolved.GetProperty("performanceOrFanModeReadback").GetBoolean());
    }

    [Fact]
    public void Evidence_RecordsUnassignedStateBearingQueryCandidates()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement evidence = document.RootElement.GetProperty("performanceFanRefreshEvidence");
        JsonElement[] candidates = evidence.GetProperty("stateBearingQueryCandidates")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(["0D/8E", "0D/8F", "0D/80", "0D/89", "0D/8B", "0D/8A"],
            candidates.Select(candidate => candidate.GetProperty("command").GetString()!).ToArray());
        Assert.Equal([557], Frames(candidates[0], "requestFrames"));
        Assert.Equal([560], Frames(candidates[0], "responseFrames"));
        Assert.Equal("00", candidates[0].GetProperty("responsePayloadHex").GetString());
        Assert.Equal("01", candidates[1].GetProperty("responsePayloadHex").GetString());
        Assert.Equal("020102", candidates[2].GetProperty("responsePayloadPrefixHex").GetString());
        Assert.Equal([569, 751], Frames(candidates[3], "requestFrames"));
        Assert.Equal([572, 754], Frames(candidates[3], "responseFrames"));
        Assert.Equal("0202", candidates[3].GetProperty("responsePayloadHex").GetString());
        Assert.Equal("0201", candidates[4].GetProperty("responsePayloadHex").GetString());
        Assert.Equal("0206", candidates[5].GetProperty("responsePayloadHex").GetString());
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("02", candidate.GetProperty("responseStatusHex").GetString());
            Assert.Equal(
                "unassigned-single-state-query-candidate",
                candidate.GetProperty("semantic").GetString());
        });

        JsonElement discriminator = evidence.GetProperty("candidateDiscriminator");
        Assert.Equal(
            ["Balanced+Auto", "Silent+Auto", "Performance+Auto", "Balanced+Maximum"],
            discriminator.GetProperty("requiredKnownStates")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
    }

    [Fact]
    public void Evidence_ScopesReadbackConclusionToCaptureAndKeepsProductionClosed()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement conclusion = document.RootElement
            .GetProperty("performanceFanRefreshEvidence")
            .GetProperty("conclusion");

        Assert.True(conclusion.GetProperty("queryShapedStateResponsesObservedInCapture").GetBoolean());
        Assert.False(conclusion.GetProperty("independentPerformanceOrFanModeReadbackEstablished").GetBoolean());
        Assert.False(conclusion.GetProperty("performanceFanProductionAdmissionAllowed").GetBoolean());
        Assert.False(conclusion.GetProperty("typedMutationHarnessAllowed").GetBoolean());
        Assert.False(conclusion.TryGetProperty("independentReadbackAvailable", out _));
        Assert.Contains("this capture", conclusion.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(CaptureHash)));
    }

    [Fact]
    public void Evidence_RecordsFixedHydrationCacheNegativeWithoutAdmission()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        JsonElement capture = root.GetProperty("captureSources")
            .EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == FixedHydrationCaptureId);
        JsonElement evidence = root.GetProperty("fanFixedHydrationEvidence");

        Assert.Equal(FixedHydrationCaptureHash, capture.GetProperty("sha256").GetString());
        Assert.Equal(265407, capture.GetProperty("bytes").GetInt32());
        Assert.Equal(2606, capture.GetProperty("frameCount").GetInt32());
        Assert.Equal(FixedHydrationCaptureId, evidence.GetProperty("capture").GetString());
        Assert.Equal(0, evidence.GetProperty("candidateCommandOccurrences").GetInt32());
        Assert.False(evidence.GetProperty("conclusion")
            .GetProperty("performanceFanProductionAdmissionAllowed").GetBoolean());
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(
            Convert.FromHexString(FixedHydrationCaptureHash)));
    }

    [Fact]
    public void DirectQueryFixture_DisqualifiesCandidatesAcrossFourKnownStates()
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(DirectQueryFixturePath()));
        JsonElement root = fixture.RootElement;
        JsonElement[] runs = root.GetProperty("runs").EnumerateArray().ToArray();

        Assert.Equal(
            ["Balanced+Auto", "Silent+Auto", "Performance+Auto", "Balanced+Fixed5400"],
            runs.Select(run => run.GetProperty("condition").GetString()!).ToArray());
        Assert.All(runs, run => Assert.True(run.GetProperty("visuallyConfirmed").GetBoolean()));

        string baseline = runs[0].GetProperty("responses").GetRawText();
        Assert.All(runs.Skip(1), run => Assert.Equal(
            baseline,
            run.GetProperty("responses").GetRawText()));
        Assert.All(runs.SelectMany(run => run.GetProperty("responses").EnumerateArray()),
            response => Assert.Equal("02", response.GetProperty("expectedStatusHex").GetString()));

        JsonElement conclusion = root.GetProperty("conclusion");
        Assert.True(conclusion.GetProperty("allResponsesIdentical").GetBoolean());
        Assert.False(conclusion.GetProperty("performanceReadbackEstablished").GetBoolean());
        Assert.False(conclusion.GetProperty("fanModeReadbackEstablished").GetBoolean());
        Assert.False(conclusion.GetProperty("performanceFanProductionAdmissionAllowed").GetBoolean());

        using JsonDocument evidenceDocument = LoadEvidence();
        JsonElement evidence = evidenceDocument.RootElement
            .GetProperty("performanceFanDirectQueryEvidence");
        Assert.True(evidence.GetProperty("readOnly").GetBoolean());
        Assert.False(evidence.GetProperty("conclusion")
            .GetProperty("performanceFanProductionAdmissionAllowed").GetBoolean());
        Assert.False(evidence.GetProperty("conclusion")
            .GetProperty("typedMutationHarnessAllowed").GetBoolean());
    }

    [Fact]
    public void Evidence_RecordsColdHydrationCandidateWithoutAdmission()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement root = document.RootElement;
        JsonElement capture = root.GetProperty("captureSources")
            .EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == ColdHydrationCaptureId);
        JsonElement evidence = root.GetProperty("performanceFanColdHydrationEvidence");
        JsonElement candidate = evidence.GetProperty("newCandidate0F_98");

        Assert.Equal(ColdHydrationCaptureHash, capture.GetProperty("sha256").GetString());
        Assert.Equal(641695, capture.GetProperty("bytes").GetInt32());
        Assert.Equal(4822, capture.GetProperty("frameCount").GetInt32());
        Assert.Equal(ColdHydrationCaptureId, evidence.GetProperty("capture").GetString());
        Assert.Equal(0, evidence.GetProperty("candidateCommandOccurrences").GetInt32());
        Assert.Equal([2061, 2139], Frames(candidate, "requestFrames"));
        Assert.Equal([2064, 2142], Frames(candidate, "responseFrames"));
        Assert.Equal("00", candidate.GetProperty("responsePayloadHex").GetString());
        Assert.False(candidate.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(evidence.GetProperty("conclusion")
            .GetProperty("performanceFanProductionAdmissionAllowed").GetBoolean());
        Assert.False(evidence.GetProperty("conclusion")
            .GetProperty("typedMutationHarnessAllowed").GetBoolean());
        Assert.False(TrustedFixtureHashRegistry.Production.Contains(
            Convert.FromHexString(ColdHydrationCaptureHash)));
    }

    private static JsonDocument LoadEvidence() => JsonDocument.Parse(File.ReadAllText(EvidencePath()));

    private static int[] Frames(JsonElement element, string propertyName) => element
        .GetProperty(propertyName)
        .EnumerateArray()
        .Select(frame => frame.GetInt32())
        .ToArray();

    private static string EvidencePath() => RepositoryFile(
        "captures", "decoded", "rz09-0581-pid-02e0-bios-3.01-evidence.json");

    private static string AnnotationPath() => RepositoryFile(
        "captures", "annotations", "2026-07-14-performance-fan-state-refresh-negative.json");

    private static string DirectQueryFixturePath() => RepositoryFile(
        "captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-performance-fan-discriminator-negative.json");

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
