using System.Text.Json;

namespace OpenBlade.Protocol.Tests;

public sealed class ExtendedControlsEvidenceTests
{
    [Fact]
    public void HistoricalCapture_ObservedNoFunctionKeyOrGamingModeGetter()
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
    public void FunctionKeyReadbackEvidence_AdmitsOnlyTheDiscriminatedExactQuery()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            FunctionKeyReadbackEvidencePath()));
        JsonElement root = document.RootElement;
        JsonElement operation = root.GetProperty("operation");
        JsonElement result = root.GetProperty("result");

        Assert.Equal("02/86", operation.GetProperty("command").GetString());
        Assert.Equal("0100", operation.GetProperty("requestPayloadHex").GetString());
        Assert.Equal("FunctionKeys", root.GetProperty("mapping").GetProperty("0100").GetString());
        Assert.Equal("MultimediaKeys", root.GetProperty("mapping").GetProperty("0101").GetString());
        Assert.True(result.GetProperty("stateDiscriminationValidated").GetBoolean());
        Assert.True(result.GetProperty("semanticMappingConfirmed").GetBoolean());
        Assert.True(result.GetProperty("productionReadbackAdmitted").GetBoolean());
        Assert.False(result.GetProperty("gamingModeReadbackValidated").GetBoolean());
    }

    [Fact]
    public void GamingModeReadbackEvidence_AdmitsOnlyTheDiscriminatedExactQuery()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            GamingModeReadbackEvidencePath()));
        JsonElement root = document.RootElement;
        JsonElement operation = root.GetProperty("operation");
        JsonElement result = root.GetProperty("result");

        Assert.Equal("00/88", operation.GetProperty("command").GetString());
        Assert.Equal("00000000", operation.GetProperty("requestPayloadHex").GetString());
        Assert.Equal("Off", root.GetProperty("mapping").GetProperty("00000000").GetString());
        Assert.Equal("On", root.GetProperty("mapping").GetProperty("01000000").GetString());
        Assert.True(result.GetProperty("stateDiscriminationValidated").GetBoolean());
        Assert.True(result.GetProperty("semanticMappingConfirmed").GetBoolean());
        Assert.True(result.GetProperty("productionReadbackAdmitted").GetBoolean());
        Assert.Equal(
            "Off",
            root.GetProperty("restoration").GetProperty("confirmedFinalState").GetString());
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

    [Fact]
    public void SynapseStateOwnershipEvidence_ProvesSavedSetterReplayNotFirmwareReadback()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            SynapseStateOwnershipEvidencePath()));
        JsonElement root = document.RootElement;
        JsonElement hydration = root.GetProperty("pageHydration");
        JsonElement discrimination = root.GetProperty("stateDiscrimination");
        JsonElement decision = root.GetProperty("decision");

        Assert.Equal(
            ["00/08", "02/06"],
            hydration.GetProperty("observedActions")
                .EnumerateArray()
                .Select(action => action.GetProperty("command").GetString()!)
                .ToArray());

        JsonElement functionKey = discrimination.GetProperty("functionKeyPrimary");
        Assert.Equal("02/86", functionKey.GetProperty("candidateIndependentQuery").GetString());
        Assert.False(functionKey.GetProperty("candidateIndependentQueryObserved").GetBoolean());
        Assert.False(functionKey.GetProperty("candidateIndependentQuerySentByOpenBlade").GetBoolean());

        JsonElement gamingMode = discrimination.GetProperty("gamingMode");
        Assert.Equal("00/88", gamingMode.GetProperty("candidateIndependentQuery").GetString());
        Assert.False(gamingMode.GetProperty("candidateIndependentQueryObserved").GetBoolean());
        Assert.False(gamingMode.GetProperty("candidateIndependentQuerySentByOpenBlade").GetBoolean());

        Assert.False(decision.GetProperty("currentFirmwareReadbackProven").GetBoolean());
        Assert.False(decision.GetProperty("openBladeMustPresentAsConfirmedCurrentState").GetBoolean());
        Assert.False(decision.GetProperty("automaticReplayAllowed").GetBoolean());
        Assert.False(decision.GetProperty("guessedGetterProbeAllowed").GetBoolean());
    }

    private static string EvidencePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-extended-controls-oracle-summary.json");

    private static string SynapseStateOwnershipEvidencePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-synapse-state-ownership.json");

    private static string FunctionKeyReadbackEvidencePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-function-key-primary-readback.json");

    private static string GamingModeReadbackEvidencePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-gaming-mode-readback.json");

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
