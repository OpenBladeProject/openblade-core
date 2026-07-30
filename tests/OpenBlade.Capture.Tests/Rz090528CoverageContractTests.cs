using System.Text.Json;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528CoverageContractTests
{
    private static readonly string[] ExpectedEvidenceFrontier =
    [
        "conflicts.vendorControllerPresent=SetterValidated",
        "failures.timeout=Captured",
        "failures.transactionMismatch=QueryValidated",
        "keyboardLighting.effects.ripple.base=Captured",
        "keyboardLighting.effects.spectrum.base=Captured",
        "keyboardLighting.effects.starlight.base=Captured",
        "keyboardLighting.effects.static.base=Captured",
        "keyboardLighting.effects.tidal.base=Captured",
        "keyboardLighting.effects.wave.base=Captured",
        "keyboardLighting.effects.wheel.base=Captured",
        "keyboardLighting.lampArrayInterfaceDiscovery=QueryValidated",
        "performance.ac.cpuPowerLimitWatts=Captured",
        "performance.ac.cpuVoltageOptimizer=Captured",
        "performance.ac.gpuClockOffsetUiPolicy=Captured",
        "performance.ac.gpuClockOffsets=QueryValidated",
        "performance.ac.gpuOverclockToggle=Captured",
        "performance.battery.presets=Captured",
        "performance.usbC.powerClasses=Captured",
        "performance.usbC.presetsByPowerClass=Captured",
        "power.acToBattery=Captured",
        "power.acToUsbC=Captured",
        "power.battery=Captured",
        "power.batteryToAc=Captured",
        "power.batteryToUsbC=Captured",
        "power.usbCPowerClasses=Captured",
        "power.usbCToAc=Captured",
        "power.usbCToBattery=Captured",
        "specialKeys.functionLayerReports=Captured",
        "specialKeys.m1PageUp=Captured",
        "specialKeys.m2PageDown=Captured",
        "specialKeys.m3GamingMode=Captured",
        "specialKeys.m4PerformanceMode=Captured",
        "specialKeys.m5MicrophoneMute=Captured",
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

        Assert.Equal(53, counts["ProductionAdmitted"]);
        Assert.Equal(3, counts["QueryValidated"]);
        Assert.Equal(30, counts["Captured"]);
        Assert.Equal(1, counts["SetterValidated"]);
        Assert.Equal(10, counts["SoftwareOnly"]);
        Assert.Equal(3, counts["Absent"]);
        Assert.Equal(44, counts["NotInvestigated"]);
    }

    [Fact]
    public void EvidenceCheckpoint_IdentifiesCanonicalCaptureRevision()
    {
        using JsonDocument document = LoadCoverage();
        JsonElement root = document.RootElement;
        JsonElement source = root.GetProperty("source");
        JsonElement device = root.GetProperty("device");

        Assert.Equal("openblade-captures", source.GetProperty("repository").GetString());
        Assert.Equal("04281c0", source.GetProperty("revision").GetString());
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
