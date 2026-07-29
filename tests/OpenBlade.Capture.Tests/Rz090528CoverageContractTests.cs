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

        Assert.Equal(48, counts["ProductionAdmitted"]);
        Assert.Equal(2, counts["QueryValidated"]);
        Assert.Equal(23, counts["Captured"]);
        Assert.Equal(1, counts["SetterValidated"]);
        Assert.Equal(10, counts["SoftwareOnly"]);
        Assert.Equal(3, counts["Absent"]);
        Assert.Equal(54, counts["NotInvestigated"]);
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
            "captures",
            "decoded",
            "rz09-0528-pid-02c6-bios-2.02-device-coverage.json")));

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
