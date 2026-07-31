using System.Text.Json;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528CoverageContractTests
{
    private static readonly string[] ExpectedEvidenceFrontier =
    [
        "conflicts.vendorControllerPresent=SetterValidated",
        "failures.timeout=Captured",
        "failures.transactionMismatch=QueryValidated",
        "keyboardLighting.effects.ambientAwareness.base=Captured",
        "keyboardLighting.effects.ambientAwareness.frameCadence=Captured",
        "keyboardLighting.effects.ambientAwareness.matrixFormat=Captured",
        "keyboardLighting.effects.ambientAwareness.screenRegions=Captured",
        "keyboardLighting.effects.audioMeter.base=Captured",
        "keyboardLighting.effects.audioMeter.colorBoost=Captured",
        "keyboardLighting.effects.breathing.base=Captured",
        "keyboardLighting.effects.breathing.dualColor=Captured",
        "keyboardLighting.effects.breathing.randomColor=Captured",
        "keyboardLighting.effects.breathing.singleColor=Captured",
        "keyboardLighting.effects.fire.base=Captured",
        "keyboardLighting.effects.reactive.base=Captured",
        "keyboardLighting.effects.reactive.colors=Captured",
        "keyboardLighting.effects.reactive.durations=Captured",
        "keyboardLighting.effects.reactive.keyMap=Captured",
        "keyboardLighting.effects.ripple.base=Captured",
        "keyboardLighting.effects.ripple.colors=Captured",
        "keyboardLighting.effects.ripple.keyMap=Captured",
        "keyboardLighting.effects.spectrum.base=SetterValidated",
        "keyboardLighting.effects.spectrum.frameCadence=Captured",
        "keyboardLighting.effects.spectrum.hostColorSequence=Captured",
        "keyboardLighting.effects.starlight.base=Captured",
        "keyboardLighting.effects.starlight.dualColor=Captured",
        "keyboardLighting.effects.starlight.durations=Captured",
        "keyboardLighting.effects.starlight.randomColor=Captured",
        "keyboardLighting.effects.starlight.singleColor=Captured",
        "keyboardLighting.effects.tidal.base=Captured",
        "keyboardLighting.effects.tidal.inwardDirection=Captured",
        "keyboardLighting.effects.tidal.outwardDirection=Captured",
        "keyboardLighting.effects.tidal.primaryColor=Captured",
        "keyboardLighting.effects.tidal.randomColor=Captured",
        "keyboardLighting.effects.tidal.secondaryColor=Captured",
        "keyboardLighting.effects.wave.base=Captured",
        "keyboardLighting.effects.wave.leftDirection=Captured",
        "keyboardLighting.effects.wave.rightDirection=Captured",
        "keyboardLighting.effects.wheel.base=Captured",
        "keyboardLighting.effects.wheel.leftDirection=Captured",
        "keyboardLighting.effects.wheel.rightDirection=Captured",
        "keyboardLighting.lampArrayInterfaceDiscovery=QueryValidated",
        "performance.ac.cpuPowerLimitWatts=Captured",
        "performance.ac.cpuVoltageOptimizer=Captured",
        "performance.ac.gpuClockOffsetUiPolicy=Captured",
        "performance.ac.gpuClockOffsets=QueryValidated",
        "performance.ac.gpuOverclockToggle=Captured",
        "performance.battery.presets=Captured",
        "performance.battery.readback=QueryValidated",
        "performance.usbC.presetsByPowerClass=Captured",
        "performance.usbC.readback=QueryValidated",
        "power.acToBattery=QueryValidated",
        "power.acToUsbC=Captured",
        "power.batteryToAc=Captured",
        "power.batteryToUsbC=QueryValidated",
        "power.usbCToAc=QueryValidated",
        "power.usbCToBattery=Captured",
        "specialKeys.functionLayerReports=Captured",
        "specialKeys.m1PageUp=Captured",
        "specialKeys.m2PageDown=Captured",
        "specialKeys.performanceKey=Captured",
    ];

    [Fact]
    public void EvidenceFrontier_IsExplicitlyAccountedFor()
    {
        using JsonDocument document = LoadCoverage();
        string[] actual = Flatten(document.RootElement.GetProperty("capabilities"))
            .Where(item => item.Status is
                "Captured" or
                "QueryValidated" or
                "SetterValidated")
            .Select(item => $"{item.Path}={item.Status}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedEvidenceFrontier, actual);
    }

    [Fact]
    public void EvidenceSummary_MatchesTheMigrationCheckpoint()
    {
        using JsonDocument document = LoadCoverage();
        Dictionary<string, int> counts = Flatten(
                document.RootElement.GetProperty("capabilities"))
            .GroupBy(item => item.Status, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);

        Assert.Equal(61, counts["ProductionAdmitted"]);
        Assert.Equal(8, counts["QueryValidated"]);
        Assert.Equal(51, counts["Captured"]);
        Assert.Equal(2, counts["SetterValidated"]);
        Assert.Equal(10, counts["SoftwareOnly"]);
        Assert.Equal(5, counts["Absent"]);
        Assert.Equal(10, counts["NotInvestigated"]);
    }

    [Fact]
    public void EvidenceCheckpoint_IdentifiesCanonicalCaptureRevision()
    {
        using JsonDocument document = LoadCoverage();
        JsonElement root = document.RootElement;
        JsonElement source = root.GetProperty("source");
        JsonElement device = root.GetProperty("device");

        Assert.Equal("openblade-captures", source.GetProperty("repository").GetString());
        Assert.Equal("c5fec75", source.GetProperty("revision").GetString());
        Assert.Equal("RZ09-0528", device.GetProperty("modelNumber").GetString());
        Assert.Equal("1532", device.GetProperty("vendorIdHex").GetString());
        Assert.Equal("02C6", device.GetProperty("productIdHex").GetString());
        Assert.Equal("2.02", device.GetProperty("bios").GetString());
        Assert.Equal(
            "ProductionAdmitted",
            root.GetProperty("capabilities")
                .GetProperty("sensors")
                .GetProperty("cpuTemperature")
                .GetString());
        Assert.Equal(
            "ProductionAdmitted",
            root.GetProperty("capabilities")
                .GetProperty("lifecycle")
                .GetProperty("serviceRestart")
                .GetString());
    }

    private static IEnumerable<CoverageItem> Flatten(
        JsonElement element,
        string prefix = "")
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string path = string.IsNullOrEmpty(prefix)
                ? property.Name
                : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (CoverageItem item in Flatten(property.Value, path))
                {
                    yield return item;
                }
            }
            else
            {
                yield return new CoverageItem(
                    path,
                    property.Value.GetString()
                        ?? throw new InvalidDataException(
                            $"Coverage leaf '{path}' has no status."));
            }
        }
    }

    private static JsonDocument LoadCoverage() => JsonDocument.Parse(
        File.ReadAllText(RepositoryFile(
            "tests",
            "OpenBlade.Capture.Tests",
            "Fixtures",
            "Rz090528CoverageCheckpoint.json")));

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

    private sealed record CoverageItem(string Path, string Status);
}
