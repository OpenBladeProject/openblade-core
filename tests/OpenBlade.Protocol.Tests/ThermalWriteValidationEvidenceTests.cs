using System.Text.Json;

namespace OpenBlade.Protocol.Tests;

public sealed class ThermalWriteValidationEvidenceTests
{
    private static readonly string[] ExpectedSourceHashes =
    [
        "7031A2A9CEE1F63718BEE5856EC88CB054DA915FFBE3EF264E4ADD833B64AD67",
        "0EB7927E68ED6DAD6A16BC9779D71F34F7B55254EF4510B7DE311D806933E6BD",
        "ACD96854A6A85EB975238A5F4A1B2F8256B3B6B9FB8B93E56DE2593EEE3711ED",
        "49F54BD18C220586212E7FC774AC717FE64EB0CBA04DCBCE85C376E740B732BC",
        "F6BBB560FCF0DB8E93DD3FCFAE2F2F060D00A89ECD41E79CBF67E46057507647",
    ];

    [Fact]
    public void Fixture_IsSanitizedAndIdentifiesTheExactTarget()
    {
        using JsonDocument document = LoadFixture();
        JsonElement root = document.RootElement;
        JsonElement device = root.GetProperty("device");

        Assert.Equal("RZ09-0581", device.GetProperty("model").GetString());
        Assert.Equal("1532", device.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", device.GetProperty("productId").GetString());
        Assert.Equal("3.01", device.GetProperty("biosVersion").GetString());
        Assert.False(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("sanitized").GetBoolean());
        Assert.Equal(
            ["silent", "performance", "balanced", "fixed5400", "batterySaver"],
            root.GetProperty("sourceArtifacts").EnumerateArray()
                .Select(source => source.GetProperty("target").GetString()!).ToArray());
        Assert.Equal(
            ExpectedSourceHashes,
            root.GetProperty("sourceArtifacts").EnumerateArray()
                .Select(source => source.GetProperty("sha256").GetString()!).ToArray());

        string fixture = root.GetRawText();
        Assert.DoesNotContain("C:\\", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tmp/", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("probeId", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("envelope", fixture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fixture_RecordsAllFiveRunsWithExactApplyAndRestorationCommands()
    {
        using JsonDocument document = LoadFixture();
        JsonElement[] runs = document.RootElement.GetProperty("runs").EnumerateArray().ToArray();

        Assert.Equal(
            ["silent", "performance", "balanced", "fixed5400", "batterySaver"],
            runs.Select(run => run.GetProperty("target").GetString()!).ToArray());
        AssertRun(
            runs[0],
            "01010000", "01020000",
            ["0D/02:01010500", "0D/02:01020500"],
            "01010500", "01020500",
            ["0D/02:01010000", "0D/02:01020000"]);
        AssertRun(
            runs[1],
            "01010000", "01020000",
            ["0D/02:01010200", "0D/02:01020200"],
            "01010200", "01020200",
            ["0D/02:01010000", "0D/02:01020000"]);
        AssertRun(
            runs[2],
            "01010500", "01020500",
            ["0D/02:01010000", "0D/02:01020000"],
            "01010000", "01020000",
            ["0D/02:01010500", "0D/02:01020500"]);
        AssertRun(
            runs[3],
            "01010000", "01020000",
            ["0D/02:01010001", "0D/02:01020001", "0D/01:010136", "0D/01:010236"],
            "01010001", "01020001",
            ["0D/02:01010000", "0D/02:01020000"]);
        Assert.Equal("BatterySaver", runs[4].GetProperty("admittedAs").GetString());
        Assert.Equal(
            "BatteryBalanced",
            runs[4].GetProperty("restorationAdmittedAs").GetString());
        AssertRun(
            runs[4],
            "01010600", "01020600",
            ["0D/02:01010300", "0D/02:01020300"],
            "01010300", "01020300",
            ["0D/02:01010600", "0D/02:01020600"]);
        AssertPair(
            runs[4].GetProperty("postValidationRead"),
            "01010600",
            "01020600");
    }

    [Fact]
    public void Fixture_RecordsIndependentThermalAndFanReadbackCommands()
    {
        using JsonDocument document = LoadFixture();
        JsonElement readback = document.RootElement.GetProperty("readbackCommands");
        JsonElement[] thermal = readback.GetProperty("thermalPair").EnumerateArray().ToArray();
        JsonElement[] rpm = readback.GetProperty("fanRpmPair").EnumerateArray().ToArray();

        Assert.Equal(["0D/82:01010000", "0D/82:01020000"], CommandKeys(thermal));
        Assert.All(thermal, command => Assert.Equal(4, command.GetProperty("responsePayloadBytes").GetInt32()));
        Assert.Equal(["0D/88:010100", "0D/88:010200"], CommandKeys(rpm));
        Assert.All(rpm, command => Assert.Equal(3, command.GetProperty("responsePayloadBytes").GetInt32()));
    }

    [Fact]
    public void Fixture_RequiresAndRecordsMaximumPhysicalEffect()
    {
        using JsonDocument document = LoadFixture();
        JsonElement maximum = document.RootElement.GetProperty("runs")[3];
        JsonElement physical = maximum.GetProperty("physicalGate");
        JsonElement rule = physical.GetProperty("thresholdRule");
        JsonElement before = physical.GetProperty("preThresholdSample");
        JsonElement[] trace = physical.GetProperty("qualifyingTrace").EnumerateArray().ToArray();
        JsonElement final = physical.GetProperty("finalRpm");

        Assert.Equal("fixed5400", maximum.GetProperty("target").GetString());
        Assert.Equal("Maximum", maximum.GetProperty("admittedAs").GetString());
        Assert.Equal(95, physical.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1000, physical.GetProperty("sampleIntervalMilliseconds").GetInt32());
        Assert.Equal(5000, rule.GetProperty("minimumRpmInclusive").GetInt32());
        Assert.Equal(6000, rule.GetProperty("maximumRpmInclusive").GetInt32());
        Assert.Equal(3, rule.GetProperty("requiredConsecutiveSamples").GetInt32());
        Assert.True(rule.GetProperty("bothSelectorsMustPass").GetBoolean());

        Assert.Equal(92, before.GetProperty("sample").GetInt32());
        Assert.Equal(4900, before.GetProperty("selector2Rpm").GetInt32());
        Assert.Equal([93, 94, 95], trace.Select(sample => sample.GetProperty("sample").GetInt32()).ToArray());
        Assert.All(trace, sample =>
        {
            Assert.InRange(sample.GetProperty("selector1Rpm").GetInt32(), 5000, 6000);
            Assert.InRange(sample.GetProperty("selector2Rpm").GetInt32(), 5000, 6000);
        });
        Assert.Equal(5200, final.GetProperty("selector1Rpm").GetInt32());
        Assert.Equal(5000, final.GetProperty("selector2Rpm").GetInt32());
        Assert.Equal(5200, final.GetProperty("maxRpm").GetInt32());
        Assert.True(physical.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void Fixture_AdmitsTheValidatedPresetsAndFanModes()
    {
        using JsonDocument document = LoadFixture();
        JsonElement root = document.RootElement;
        JsonElement[] runs = root.GetProperty("runs").EnumerateArray().ToArray();
        JsonElement admission = root.GetProperty("admission");

        Assert.All(runs, run =>
        {
            JsonElement gates = run.GetProperty("gates");
            Assert.True(gates.GetProperty("applyAttempted").GetBoolean());
            Assert.True(gates.GetProperty("applyAcknowledged").GetBoolean());
            Assert.True(gates.GetProperty("candidateConfirmed").GetBoolean());
            Assert.True(gates.GetProperty("restoreAttempted").GetBoolean());
            Assert.True(gates.GetProperty("restoreAcknowledged").GetBoolean());
            Assert.True(gates.GetProperty("restored").GetBoolean());
            Assert.False(gates.GetProperty("indeterminatePartialWrite").GetBoolean());
            Assert.False(gates.GetProperty("manualRecoveryRequired").GetBoolean());
            Assert.Equal("none", gates.GetProperty("failure").GetString());
            Assert.True(gates.GetProperty("success").GetBoolean());
        });

        Assert.True(admission.GetProperty("exactDeviceOnly").GetBoolean());
        Assert.Equal(
            ["Silent", "Balanced", "Performance", "BatterySaver", "BatteryBalanced"],
            admission.GetProperty("performancePresets").EnumerateArray()
                .Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            ["Automatic", "Maximum"],
            admission.GetProperty("fanModes").EnumerateArray()
                .Select(value => value.GetString()!).ToArray());
        Assert.Equal("fixed-5400-rpm-sequence", admission.GetProperty("maximumImplementation").GetString());
        Assert.True(admission.GetProperty("allRunsRestoredWithoutManualRecovery").GetBoolean());
    }

    private static void AssertRun(
        JsonElement run,
        string baseline1,
        string baseline2,
        string[] expectedApply,
        string candidate1,
        string candidate2,
        string[] expectedRestoration)
    {
        AssertPair(run.GetProperty("baseline"), baseline1, baseline2);
        Assert.Equal(expectedApply, ActionKeys(run.GetProperty("applyActions")));
        AssertPair(run.GetProperty("candidate"), candidate1, candidate2);
        Assert.Equal(expectedRestoration, ActionKeys(run.GetProperty("restorationActions")));
        AssertPair(run.GetProperty("restoration"), baseline1, baseline2);

        foreach (JsonElement action in run.GetProperty("applyActions").EnumerateArray()
                     .Concat(run.GetProperty("restorationActions").EnumerateArray()))
        {
            Assert.Equal("02", action.GetProperty("statusHex").GetString());
            Assert.Equal(action.GetProperty("payloadHex").GetString(), action.GetProperty("echoHex").GetString());
        }
    }

    private static void AssertPair(JsonElement pair, string thermal1, string thermal2)
    {
        Assert.Equal(thermal1, pair.GetProperty("thermal1Hex").GetString());
        Assert.Equal(thermal2, pair.GetProperty("thermal2Hex").GetString());
    }

    private static string[] ActionKeys(JsonElement actions) => actions.EnumerateArray()
        .Select(action => $"{action.GetProperty("classHex").GetString()}/{action.GetProperty("idHex").GetString()}:{action.GetProperty("payloadHex").GetString()}")
        .ToArray();

    private static string[] CommandKeys(IEnumerable<JsonElement> commands) => commands
        .Select(command => $"{command.GetProperty("classHex").GetString()}/{command.GetProperty("idHex").GetString()}:{command.GetProperty("requestPayloadHex").GetString()}")
        .ToArray();

    private static JsonDocument LoadFixture() => JsonDocument.Parse(File.ReadAllText(FixturePath()));

    private static string FixturePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-thermal-write-validation-fixture.json");

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
