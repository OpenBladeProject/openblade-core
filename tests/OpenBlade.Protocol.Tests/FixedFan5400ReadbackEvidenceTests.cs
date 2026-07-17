using System.Text.Json;

namespace OpenBlade.Protocol.Tests;

public sealed class FixedFan5400ReadbackEvidenceTests
{
    [Fact]
    public void Fixture_IdentifiesExactTargetAndContainsNoMachineLocalArtifacts()
    {
        using JsonDocument document = LoadFixture();
        JsonElement root = document.RootElement;
        JsonElement device = root.GetProperty("device");

        Assert.Equal("RZ09-0581", device.GetProperty("model").GetString());
        Assert.Equal("1532", device.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", device.GetProperty("productId").GetString());
        Assert.Equal("3.01", device.GetProperty("biosVersion").GetString());
        Assert.True(root.GetProperty("readOnly").GetBoolean());

        string fixture = root.GetRawText();
        Assert.DoesNotContain("C:\\", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tmp/", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("probeId", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileName", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("envelope", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", fixture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fixture_RecordsThreeRepeatableFixedSamplesAndExactFanResponse()
    {
        using JsonDocument document = LoadFixture();
        JsonElement fixedRun = document.RootElement.GetProperty("runs")[0];
        JsonElement[] samples = fixedRun.GetProperty("samples").EnumerateArray().ToArray();

        Assert.Equal("Synapse Fixed Fan Speed 5400", fixedRun.GetProperty("condition").GetString());
        Assert.Equal(3, samples.Length);
        Assert.All(samples, sample =>
        {
            Assert.Equal("02", sample.GetProperty("statusHex").GetString());
            Assert.Equal("00", sample.GetProperty("powerControlHex").GetString());
            Assert.Equal("01010401", sample.GetProperty("thermal1Hex").GetString());
            Assert.Equal("01020401", sample.GetProperty("thermal2Hex").GetString());
            Assert.Equal(5300, sample.GetProperty("fan1Rpm").GetInt32());
            Assert.Equal(5300, sample.GetProperty("fan2Rpm").GetInt32());
        });
    }

    [Fact]
    public void Fixture_RecordsBalancedAutoRestorationAndObservedRpmSequence()
    {
        using JsonDocument document = LoadFixture();
        JsonElement restoredRun = document.RootElement.GetProperty("runs")[1];
        JsonElement[] samples = restoredRun.GetProperty("samples").EnumerateArray().ToArray();

        Assert.Equal("Balanced+Auto restored", restoredRun.GetProperty("condition").GetString());
        Assert.Equal(3, samples.Length);
        Assert.All(samples, sample =>
        {
            Assert.Equal("02", sample.GetProperty("statusHex").GetString());
            Assert.Equal("00", sample.GetProperty("powerControlHex").GetString());
            Assert.Equal("01010000", sample.GetProperty("thermal1Hex").GetString());
            Assert.Equal("01020000", sample.GetProperty("thermal2Hex").GetString());
        });
        Assert.Equal([4000, 4000, 3900], samples.Select(sample => sample.GetProperty("fan1Rpm").GetInt32()).ToArray());
        Assert.Equal([4000, 3900, 3900], samples.Select(sample => sample.GetProperty("fan2Rpm").GetInt32()).ToArray());
    }

    [Fact]
    public void Fixture_ProvesOnlyReadbackAndKeepsAllWriteAdmissionClosed()
    {
        using JsonDocument document = LoadFixture();
        JsonElement root = document.RootElement;
        JsonElement semantics = root.GetProperty("decodedSemantics");
        JsonElement powerControl = root.GetProperty("powerControlBoundary");
        JsonElement conclusion = root.GetProperty("conclusion");

        Assert.Equal("01", semantics.GetProperty("fixedFanModeValueHex").GetString());
        Assert.Equal("00", semantics.GetProperty("autoFanModeValueHex").GetString());
        Assert.Equal("04", semantics.GetProperty("fixedStateValueHex").GetString());
        Assert.Equal("00", semantics.GetProperty("restoredBalancedStateValueHex").GetString());
        Assert.Equal(5400, semantics.GetProperty("requestedFixedRpm").GetInt32());
        Assert.Equal(5300, semantics.GetProperty("observedFixedRpm").GetInt32());

        Assert.Equal("00", powerControl.GetProperty("observedValueHex").GetString());
        Assert.Equal("02", powerControl.GetProperty("genericMaximumFanBitMaskHex").GetString());
        Assert.False(powerControl.GetProperty("genericMaximumFanBitObserved").GetBoolean());
        Assert.False(powerControl.GetProperty("genericMaximumFanBitSupportedForThisDevice").GetBoolean());
        Assert.Equal("reserved-and-unsupported", powerControl.GetProperty("treatment").GetString());

        Assert.True(conclusion.GetProperty("fixedFan5400LiveReadbackEstablished").GetBoolean());
        Assert.True(conclusion.GetProperty("balancedAutoRestorationEstablished").GetBoolean());
        Assert.True(conclusion.GetProperty("fixedAndAutoStatesAreDistinct").GetBoolean());
        Assert.True(conclusion.GetProperty("fanRpmResponseEstablished").GetBoolean());
        Assert.False(root.GetProperty("productionWriteAdmitted").GetBoolean());
        Assert.False(conclusion.GetProperty("productionWriteAdmitted").GetBoolean());
        Assert.True(conclusion.GetProperty("productionWriteAdmissionRequiresOpenBladeSetterHarness").GetBoolean());
        Assert.False(conclusion.GetProperty("genericMaximumFanBitProductionAdmitted").GetBoolean());
    }

    private static JsonDocument LoadFixture() => JsonDocument.Parse(File.ReadAllText(FixturePath()));

    private static string FixturePath() => RepositoryFile(
        "captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-fixed-fan-5400-readback-fixture.json");

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
