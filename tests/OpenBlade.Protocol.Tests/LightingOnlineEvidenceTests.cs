using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class LightingOnlineEvidenceTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedCaptureHashes =
        new Dictionary<string, string>
        {
            ["lighting-static-online"] = "76CA001583EC41F2ABE53872278686A8E5CA3419F6D0583BD6E3802CD2400B0A",
            ["lighting-spectrum-online"] = "00F3D449110411497DB894A07009BDF5B13B183A2931A129C8189BC7AE921171",
            ["lighting-breathing-online"] = "30B199AD90D675223E29D0B31E8DD9F786AB248282B1F9CA3FC23A283693FE31",
            ["lighting-reactive-online"] = "84545D9F5214B1650EF931F9CAD8A0A9ED8B914CBA06567FDA4064967A3828B6",
            ["lighting-off-online"] = "BB4F15926F7D896586109EC45C6E56EAB18550226B4E65C302E6329E81CE8870",
            ["lighting-brightness-50-online"] = "4ED4F52BDDC14C72758405F867F8DE533F11E9F68346E00CD12A7DD0EC77CC8A",
            ["lighting-hydration-100-negative"] = "46D7F69422F9D182A8AF507A98C3970B91772C7D78304CD853AB4FBF28D0E4B9",
            ["lighting-hydration-49-negative"] = "848FBE79F2D1587D75E1C0B0E13508B97E910B691667A2A34659873B0715BAF3",
        };

    [Fact]
    public void Evidence_RecordsExactOnlineLightingCaptureHashesOutsideProductionRegistry()
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
        foreach (string hash in ExpectedCaptureHashes.Values)
        {
            Assert.False(TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(hash)));
        }
    }

    [Fact]
    public void Evidence_RecordsOffAsDecodedBrightnessZeroWithoutValidationAdmission()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement action = FindAction(document.RootElement, "lighting-off");
        JsonElement command = Assert.Single(action.GetProperty("commands").EnumerateArray());
        JsonElement online = action.GetProperty("onlineEvidence");
        JsonElement gates = action.GetProperty("gates");
        JsonElement provenance = document.RootElement.GetProperty("frameProvenance").GetProperty("lighting-off-online");

        Assert.Equal("03", command.GetProperty("class").GetString());
        Assert.Equal("03", command.GetProperty("id").GetString());
        Assert.Equal("010500", command.GetProperty("payloadHex").GetString());
        Assert.Equal("07", command.GetProperty("checksumHex").GetString());
        Assert.Equal("brightness-zero-not-effect-disable", online.GetProperty("semantic").GetString());
        Assert.True(online.GetProperty("interface3StreamStopped").GetBoolean());
        Assert.True(online.GetProperty("interface3StreamAbsentForRemainder").GetBoolean());
        Assert.Equal([153], Frames(provenance, "setFrames"));
        Assert.Equal([156], Frames(provenance, "status02EchoFrames"));
        Assert.Equal(151, provenance.GetProperty("lastInterface3StreamFrame").GetInt32());
        Assert.True(gates.GetProperty("captured").GetBoolean());
        Assert.True(gates.GetProperty("decoded").GetBoolean());
        Assert.False(gates.GetProperty("applied").GetBoolean());
        Assert.False(gates.GetProperty("readBack").GetBoolean());
        Assert.False(gates.GetProperty("restored").GetBoolean());
    }

    [Fact]
    public void Evidence_RecordsSharedHandshakeAndHostRenderedQuickEffects()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement quickEffects = document.RootElement.GetProperty("lightingOnlineEvidence").GetProperty("quickEffects");
        JsonElement handshake = quickEffects.GetProperty("sharedTransitionHandshake");

        Assert.Equal(["00/86", "00/84", "03/0A"], Strings(handshake, "operations"));
        Assert.Equal("0500", handshake.GetProperty("entryPayloadHex").GetString());
        Assert.False(handshake.GetProperty("identifiesEffect").GetBoolean());
        Assert.Equal("likely-generic-custom-matrix-mode-entry", handshake.GetProperty("semanticStatus").GetString());

        Dictionary<string, JsonElement> effects = quickEffects.GetProperty("effects")
            .EnumerateArray()
            .ToDictionary(effect => effect.GetProperty("name").GetString()!);
        Assert.Equal(["Static", "Spectrum", "Breathing", "Reactive"], effects.Keys);
        Assert.All(effects.Values, effect =>
        {
            Assert.True(effect.GetProperty("hostRenderedMatrixStream").GetBoolean());
            Assert.False(effect.GetProperty("nativeEffectSetterIsolated").GetBoolean());
            Assert.False(effect.GetProperty("productionAdmitted").GetBoolean());
        });

        Assert.Equal([464, 471, 475], Frames(effects["Static"], "requestFrames"));
        Assert.Equal([468, 474, 480], Frames(effects["Static"], "readbackFrames"));
        Assert.Equal([181, 185, 189], Frames(effects["Spectrum"], "requestFrames"));
        Assert.Equal([184, 188, 192], Frames(effects["Spectrum"], "readbackFrames"));
        Assert.Equal([1243, 1251, 1255, 1261], Frames(effects["Breathing"], "requestFrames"));
        Assert.Equal([1246, 1254, 1264], Frames(effects["Breathing"], "successfulReadbackFrames"));
        Assert.Equal([927, 937, 941], Frames(effects["Reactive"], "requestFrames"));
        Assert.Equal([932, 940, 944], Frames(effects["Reactive"], "readbackFrames"));
    }

    [Fact]
    public void Evidence_BoundsMatrixStreamingAsInterface3WithoutReadback()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement matrix = document.RootElement.GetProperty("lightingOnlineEvidence").GetProperty("matrixTransport");

        Assert.Equal(3, matrix.GetProperty("interfaceNumber").GetInt32());
        Assert.Equal(374, matrix.GetProperty("featureReportBytes").GetInt32());
        Assert.Equal("03", matrix.GetProperty("class").GetString());
        Assert.Equal("0B", matrix.GetProperty("id").GetString());
        Assert.Equal("0136", matrix.GetProperty("argumentDataSizeHex").GetString());
        Assert.Equal("65", matrix.GetProperty("matrixSpanEndHex").GetString());
        Assert.Equal(102, matrix.GetProperty("rgbTripletCountApprox").GetInt32());
        Assert.Equal(86, matrix.GetProperty("litPositionCount").GetInt32());
        Assert.Equal(16, matrix.GetProperty("layoutHoleCount").GetInt32());
        Assert.Equal("xor-bytes-2-through-byte-before-checksum", matrix.GetProperty("checksumRule").GetString());
        Assert.True(matrix.GetProperty("reservedFinalByteZero").GetBoolean());
        Assert.False(matrix.GetProperty("getReportReadbackExists").GetBoolean());
        Assert.False(matrix.GetProperty("productionAdmitted").GetBoolean());
        Assert.Equal("outside-safe-mvp-command-table", matrix.GetProperty("streamingBoundary").GetString());
    }

    [Fact]
    public void Evidence_SeparatesObservedBrightnessScalingFromInferredOnlyFiftyPercent()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement brightness = document.RootElement.GetProperty("lightingOnlineEvidence").GetProperty("brightness");
        JsonElement fifty = brightness.GetProperty("ui50Capture");
        JsonElement excluded = brightness.GetProperty("excludedSelector0018Reports");

        Assert.Equal("round(percent * 255 / 100)", brightness.GetProperty("mapping").GetString());
        Assert.Equal("00", brightness.GetProperty("observed").GetProperty("ui0Hex").GetString());
        Assert.Equal("7D", brightness.GetProperty("observed").GetProperty("ui49Hex").GetString());
        Assert.Equal("FF", brightness.GetProperty("observed").GetProperty("ui100Hex").GetString());
        Assert.Equal("80", fifty.GetProperty("inferredHex").GetString());
        Assert.False(fifty.GetProperty("directlyObserved").GetBoolean());
        Assert.False(fifty.GetProperty("brightnessTransactionEmitted").GetBoolean());
        Assert.False(fifty.GetProperty("ambientMatrixScaled").GetBoolean());
        Assert.Equal("0018", excluded.GetProperty("selectorHex").GetString());
        Assert.Equal("05", excluded.GetProperty("deviceProtocolStatusHex").GetString());
        Assert.Equal([871, 875, 879, 971], Frames(excluded, "requestFrames"));
        Assert.Equal([874, 878, 882, 974], Frames(excluded, "responseFrames"));
        Assert.True(excluded.GetProperty("explicitlyExcludedAsBrightnessWrites").GetBoolean());
    }

    [Fact]
    public void Evidence_RecordsBrightnessDwellWithoutTreatingAcknowledgementsAsReadback()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement dwell = document.RootElement
            .GetProperty("lightingOnlineEvidence")
            .GetProperty("brightness")
            .GetProperty("steadyStateDwell");
        JsonElement[] setters = dwell.GetProperty("setters").EnumerateArray().ToArray();
        JsonElement[] windows = dwell.GetProperty("dwellWindows").EnumerateArray().ToArray();

        Assert.Equal("main", dwell.GetProperty("capture").GetString());
        Assert.Equal([49, 100],
            setters.Select(setter => setter.GetProperty("uiPercent").GetInt32()).ToArray());
        Assert.Equal(["01057D", "0105FF"],
            setters.Select(setter => setter.GetProperty("payloadHex").GetString()!).ToArray());
        Assert.Equal([11265, 11505], Frames(setters[0], "requestFrames"));
        Assert.Equal([11268, 11508], Frames(setters[0], "responseFrames"));
        Assert.Equal([11635, 11871], Frames(setters[1], "requestFrames"));
        Assert.Equal([11638, 11874], Frames(setters[1], "responseFrames"));
        Assert.All(setters, setter =>
        {
            Assert.Equal("02", setter.GetProperty("responseStatusHex").GetString());
            Assert.True(setter.GetProperty("status02AckEchoesOnly").GetBoolean());
        });

        Assert.Equal(
            [(49, 11269, 11504, 56), (49, 11509, 11634, 30),
             (100, 11639, 11746, 26), (100, 11877, 12218, 80)],
            windows.Select(window => (
                window.GetProperty("uiPercent").GetInt32(),
                window.GetProperty("firstFrame").GetInt32(),
                window.GetProperty("lastFrame").GetInt32(),
                window.GetProperty("fanRpmPairCount").GetInt32())).ToArray());
        Assert.All(windows, window =>
            Assert.Equal(0, window.GetProperty("lightingQueryCount").GetInt32()));
        Assert.Equal("0D/88", dwell.GetProperty("onlyDwellQueryOperation").GetString());
        Assert.False(dwell.GetProperty("independentBrightnessReadbackObserved").GetBoolean());
        Assert.False(dwell.GetProperty("pageHydrationComparisonRequired").GetBoolean());
    }

    [Fact]
    public void Evidence_RecordsControlledHydrationAsNegativeForIndependentLightingReadback()
    {
        using JsonDocument document = LoadEvidence();
        JsonElement comparison = document.RootElement
            .GetProperty("lightingOnlineEvidence")
            .GetProperty("brightness")
            .GetProperty("controlledHydrationComparison");

        Assert.Equal(["lighting-hydration-100-negative", "lighting-hydration-49-negative"],
            Strings(comparison, "captures"));
        Assert.Equal([100, 49], comparison.GetProperty("brightnessStatesPercent")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.True(comparison.GetProperty("pageHydratedToExistingState").GetBoolean());
        Assert.Equal([0, 0], comparison.GetProperty("brightnessCommandCount")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([0, 0], comparison.GetProperty("lightingQueryCount")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal(["07/92", "07/8F", "0D/88"], Strings(comparison, "observedReadOperations"));
        Assert.True(comparison.GetProperty("batteryResponsesIdenticalAcrossStates").GetBoolean());
        Assert.True(comparison.GetProperty("fanResponsesAreLiveRpmTelemetry").GetBoolean());
        Assert.True(comparison.GetProperty("matrixReportsAreHostOriginatedWrites").GetBoolean());
        Assert.False(comparison.GetProperty("independentBrightnessReadbackObserved").GetBoolean());
        Assert.False(comparison.GetProperty("independentEffectReadbackObserved").GetBoolean());
        Assert.False(comparison.GetProperty("independentColorReadbackObserved").GetBoolean());
        Assert.False(comparison.GetProperty("productionAdmitted").GetBoolean());
    }

    [Fact]
    public void HydrationAnnotation_IsSanitizedAndRecordsFinalRestoration()
    {
        string text = File.ReadAllText(HydrationAnnotationPath());
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;

        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.False(root.GetProperty("rawCapturesCommitted").GetBoolean());
        Assert.All(root.GetProperty("captures").EnumerateArray(), capture =>
            Assert.Contains(capture.GetProperty("baselineBrightnessPercent").GetInt32(), new[] { 49, 100 }));
        Assert.False(root.GetProperty("decodedComparison")
            .GetProperty("independentBrightnessReadbackObserved").GetBoolean());
        Assert.True(root.GetProperty("captureShutdown").GetProperty("gracefulTargetedBreak").GetBoolean());
        Assert.False(root.GetProperty("captureShutdown").GetProperty("forceFallbackUsed").GetBoolean());
        Assert.Equal("Ambient Awareness + brightness 100",
            root.GetProperty("finalRestoration").GetProperty("state").GetString());
        Assert.False(root.TryGetProperty("usbPcapRoot", out _));
        Assert.False(root.TryGetProperty("usbDeviceAddress", out _));
        Assert.DoesNotContain("serial", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Annotation_IsSanitizedAndRecordsLightingActionOrderAndRestoration()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(AnnotationPath()));
        JsonElement root = document.RootElement;

        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.True(root.GetProperty("condition").GetProperty("acOnline").GetBoolean());
        Assert.False(root.GetProperty("rawCapturesCommitted").GetBoolean());
        Assert.Equal(
            ["Static", "Spectrum", "Breathing", "Reactive", "restore Ambient Awareness", "Off", "restore On", "UI 49", "UI 50", "restore 100"],
            Strings(root, "actionOrder"));
        JsonElement[] observations = root.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(Strings(root, "actionOrder"), observations.Select(item => item.GetProperty("action").GetString()));
        Assert.All(observations, observation => Assert.True(observation.GetProperty("visuallyConfirmed").GetBoolean()));
        Assert.All(root.GetProperty("captures").EnumerateArray(), capture =>
            Assert.True(capture.GetProperty("visuallyConfirmed").GetBoolean()));
        Assert.Equal("Ambient Awareness + brightness 100", root.GetProperty("finalRestoration").GetProperty("state").GetString());
        Assert.True(root.GetProperty("finalRestoration").GetProperty("visuallyConfirmed").GetBoolean());
        Assert.False(root.TryGetProperty("usbPcapRoot", out _));
        Assert.False(root.TryGetProperty("usbDeviceAddress", out _));
        Assert.DoesNotContain("serial", File.ReadAllText(AnnotationPath()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbientAwarenessEvidence_RecordsAllRegionsAsHostRenderedInputs()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            AmbientAwarenessEvidencePath()));
        JsonElement root = document.RootElement;
        JsonElement protocol = root.GetProperty("protocol");

        Assert.Equal(
            ["Full", "Left", "Top", "Right", "Bottom"],
            root.GetProperty("screenRegions")
                .EnumerateArray()
                .Select(region => region.GetProperty("semanticName").GetString()!)
                .ToArray());
        Assert.Equal(5, protocol.GetProperty("identicalEntrySequenceCount").GetInt32());
        Assert.Equal(
            ["00/86:000000", "00/84:0000", "03/0A:0500"],
            Strings(protocol, "entrySequence"));
        Assert.False(protocol.GetProperty("independentRegionSetterObserved").GetBoolean());
        Assert.True(protocol.GetProperty("regionEncodedInMatrixRgb").GetBoolean());
        Assert.True(root.GetProperty("restoration").GetProperty("visuallyConfirmed").GetBoolean());
        Assert.False(root.GetProperty("implementationBoundary")
            .GetProperty("productionControlAdmitted").GetBoolean());
    }

    private static JsonDocument LoadEvidence() => JsonDocument.Parse(File.ReadAllText(EvidencePath()));

    private static string EvidencePath() => RepositoryFile(
        "evidence", "legacy-captures", "decoded", "rz09-0581-pid-02e0-bios-3.01-evidence.json");

    private static string AnnotationPath() => RepositoryFile(
        "evidence", "legacy-captures", "annotations", "2026-07-14-online-lighting-session.json");

    private static string HydrationAnnotationPath() => RepositoryFile(
        "evidence", "legacy-captures", "annotations", "2026-07-16-lighting-hydration-negative.json");

    private static string AmbientAwarenessEvidencePath() => RepositoryFile(
        "evidence", "legacy-captures", "decoded",
        "rz09-0581-pid-02e0-bios-3.01-ambient-awareness-regions-oracle-summary.json");

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

    private static int[] Frames(JsonElement element, string propertyName) => element
        .GetProperty(propertyName)
        .EnumerateArray()
        .Select(frame => frame.GetInt32())
        .ToArray();

    private static string[] Strings(JsonElement element, string propertyName) => element
        .GetProperty(propertyName)
        .EnumerateArray()
        .Select(value => value.GetString()!)
        .ToArray();
}
