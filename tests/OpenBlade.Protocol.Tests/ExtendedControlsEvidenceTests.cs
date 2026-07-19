using System.Text.Json;

namespace OpenBlade.Protocol.Tests;

public sealed class ExtendedControlsEvidenceTests
{
    [Fact]
    public void Evidence_KeepsFunctionKeyAndGamingModeSetterOnly()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(EvidencePath()));
        JsonElement root = document.RootElement;
        JsonElement audit = root.GetProperty("oneShotReadbackAudit");

        Assert.Equal(66286, audit.GetProperty("frameCount").GetInt32());
        Assert.Equal(1168.83332, audit.GetProperty("captureDurationSeconds").GetDouble(), 5);
        Assert.Equal(
            ["00/84", "00/86", "07/8C", "0D/87", "0D/88"],
            Strings(audit, "observedReadCommands"));
        Assert.Equal(["0101", "0100"], Strings(audit, "functionKeySetterTransitionsObserved"));
        Assert.Equal(["01000000", "00000000"], Strings(audit, "gamingModeSetterTransitionsObserved"));
        Assert.False(audit.GetProperty("functionKeyIndependentReadObserved").GetBoolean());
        Assert.False(audit.GetProperty("gamingModeIndependentReadObserved").GetBoolean());
        Assert.False(audit.GetProperty("guessedReadCommandsSent").GetBoolean());

        Assert.False(root.GetProperty("functionKeyPrimary")
            .GetProperty("independentQueryObserved").GetBoolean());
        Assert.False(root.GetProperty("gamingMode")
            .GetProperty("independentQueryObserved").GetBoolean());
    }

    [Fact]
    public void Evidence_RecordsStartupAnimationAsTheOnlyConfirmedOneShotReadback()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(EvidencePath()));
        JsonElement startup = document.RootElement.GetProperty("startupAnimation");

        Assert.Equal("0F/98", startup.GetProperty("query").GetString());
        Assert.True(startup.GetProperty("independentQueryObserved").GetBoolean());
        Assert.True(startup.GetProperty("productionReadbackAdmitted").GetBoolean());
        Assert.Equal("00", startup.GetProperty("queryReadbacks").GetProperty("on").GetString());
        Assert.Equal("01", startup.GetProperty("queryReadbacks").GetProperty("off").GetString());
        Assert.Equal("ConfirmedReadback", startup.GetProperty("openBladeStatePresentation").GetString());
    }

    private static string EvidencePath() => RepositoryFile(
        "captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-extended-controls-oracle-summary.json");

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

    private static string[] Strings(JsonElement element, string propertyName) => element
        .GetProperty(propertyName)
        .EnumerateArray()
        .Select(value => value.GetString()!)
        .ToArray();
}
