using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PerformanceReadbackEvidenceTests
{
    private static readonly string[] ExpectedRendererHashes =
    [
        "DCEE07D4ED576D05A197E82D8232C17AAF9269C76C90E2847D55F64B5EA5051D",
        "DE416573BDFAC995DDFD7D4DCB1668FCEFC5F7AB9E586DE8FB67383A40AE36D9",
        "03202C1CA853FDC2E5B39F782DE881671F4670D3068D57A26F67065C31CB1D25",
    ];

    [Fact]
    public void Fixture_IdentifiesExactTargetAndSanitizedRendererProvenance()
    {
        using JsonDocument document = LoadFixture();
        JsonElement root = document.RootElement;
        JsonElement device = root.GetProperty("device");
        JsonElement[] sources = root.GetProperty("rendererSources").EnumerateArray().ToArray();

        Assert.Equal("RZ09-0581", device.GetProperty("model").GetString());
        Assert.Equal("1532", device.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", device.GetProperty("productId").GetString());
        Assert.Equal("3.01", device.GetProperty("biosVersion").GetString());
        Assert.Equal(
            ["38f9510dbef72388_0", "5d246080dbc9932d_0", "b94e61f2d8f88bd4_0"],
            sources.Select(source => source.GetProperty("cacheFileName").GetString()!).ToArray());
        Assert.Equal(
            ExpectedRendererHashes,
            sources.Select(source => source.GetProperty("sha256").GetString()!).ToArray());
        Assert.All(sources, source => Assert.StartsWith(
            "https://apps.razer.com/synapse/products/736/mw/",
            source.GetProperty("url").GetString(),
            StringComparison.Ordinal));

        string fixture = root.GetRawText();
        Assert.DoesNotContain("C:\\", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tmp/", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("envelope", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascriptSource", fixture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fixture_RecordsOnlyTheThreeFixedReadOnlyOperations()
    {
        using JsonDocument document = LoadFixture();
        JsonElement root = document.RootElement;
        JsonElement[] operations = root.GetProperty("fixedReadOnlyOperations").EnumerateArray().ToArray();

        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.Equal(
            ["07/8F power control", "0D/82 class1 thermal1", "0D/82 class1 thermal2"],
            operations.Select(operation => operation.GetProperty("operation").GetString()!).ToArray());
        Assert.Equal(["07", "0D", "0D"],
            operations.Select(operation => operation.GetProperty("classHex").GetString()!).ToArray());
        Assert.Equal(["8F", "82", "82"],
            operations.Select(operation => operation.GetProperty("idHex").GetString()!).ToArray());
        Assert.Equal(["00", "01010000", "01020000"],
            operations.Select(operation => operation.GetProperty("requestPayloadHex").GetString()!).ToArray());
        Assert.Equal([1, 4, 4],
            operations.Select(operation => operation.GetProperty("responseLength").GetInt32()).ToArray());
    }

    [Fact]
    public void Fixture_RecordsThreeSuccessfulSamplesPerConditionAndBothRestorations()
    {
        using JsonDocument document = LoadFixture();
        JsonElement[] runs = document.RootElement.GetProperty("runs").EnumerateArray().ToArray();

        Assert.Equal(
            [
                "Balanced+Auto",
                "Silent+Auto",
                "Balanced+Auto restored",
                "Performance+Auto",
                "Balanced+Auto final restoration",
            ],
            runs.Select(run => run.GetProperty("condition").GetString()!).ToArray());
        Assert.All(runs, run => Assert.Equal(3, run.GetProperty("samples").GetArrayLength()));
        Assert.All(
            runs.SelectMany(run => run.GetProperty("samples").EnumerateArray()),
            sample =>
            {
                Assert.Equal("02", sample.GetProperty("statusHex").GetString());
                Assert.Equal("00", sample.GetProperty("powerControlHex").GetString());
                Assert.Matches("^[0-9A-F]{64}$", sample.GetProperty("sha256").GetString()!);
            });

        AssertRunPayloads(runs[0], "01010000", "01020000");
        AssertRunPayloads(runs[1], "01010500", "01020500");
        AssertRunPayloads(runs[2], "01010000", "01020000");
        AssertRunPayloads(runs[3], "01010200", "01020200");
        AssertRunPayloads(runs[4], "01010000", "01020000");
    }

    [Fact]
    public void Fixture_EstablishesPerformanceReadbackButKeepsSetterAndFanAdmissionClosed()
    {
        using JsonDocument document = LoadFixture();
        JsonElement root = document.RootElement;
        JsonElement semantics = root.GetProperty("decodedSemantics");
        JsonElement performance = semantics.GetProperty("performanceValues");
        JsonElement fan = semantics.GetProperty("fanModeValues");
        JsonElement ui = root.GetProperty("pid736Ui");
        JsonElement conclusion = root.GetProperty("conclusion");

        Assert.Equal("00", performance.GetProperty("Balanced").GetString());
        Assert.Equal("02", performance.GetProperty("Performance").GetString());
        Assert.Equal("05", performance.GetProperty("Silent").GetString());
        Assert.Equal("00", fan.GetProperty("Auto").GetString());
        Assert.Equal("01", fan.GetProperty("Fixed").GetString());
        Assert.Equal(["Auto"], semantics.GetProperty("fanModeLiveReadbackEstablished")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.False(semantics.GetProperty("fixedFanModeLiveReadbackEstablished").GetBoolean());
        Assert.False(ui.GetProperty("maximumFanModeSupported").GetBoolean());
        Assert.False(ui.GetProperty("maximumFanModeProductionAdmitted").GetBoolean());

        Assert.True(conclusion.GetProperty("balancedSilentPerformanceReadbackEstablished").GetBoolean());
        Assert.True(conclusion.GetProperty("autoFanModeLiveReadbackEstablished").GetBoolean());
        Assert.False(conclusion.GetProperty("fixedFanModeLiveReadbackEstablished").GetBoolean());
        Assert.True(conclusion.GetProperty(
            "performanceProductionAdmissionAllowedAfterTypedSettersSeparatelyValidated").GetBoolean());
        Assert.False(conclusion.GetProperty("performanceProductionAdmitted").GetBoolean());
        Assert.False(conclusion.GetProperty("fanModeProductionAdmitted").GetBoolean());
        Assert.False(conclusion.GetProperty("maximumFanModeProductionAdmitted").GetBoolean());

        Assert.All(ExpectedRendererHashes, hash => Assert.False(
            TrustedFixtureHashRegistry.Production.Contains(Convert.FromHexString(hash))));
    }

    private static void AssertRunPayloads(JsonElement run, string thermal1, string thermal2)
    {
        Assert.All(run.GetProperty("samples").EnumerateArray(), sample =>
        {
            Assert.Equal(thermal1, sample.GetProperty("thermal1Hex").GetString());
            Assert.Equal(thermal2, sample.GetProperty("thermal2Hex").GetString());
        });
    }

    private static JsonDocument LoadFixture() => JsonDocument.Parse(File.ReadAllText(FixturePath()));

    private static string FixturePath() => RepositoryFile(
        "captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-performance-readback-fixture.json");

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
