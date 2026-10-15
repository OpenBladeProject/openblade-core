using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ExperimentalEvidenceDiagnosticsTests
{
    [Fact]
    public void OlderDiagnosticSnapshot_OmittedFeatureEvidenceDefaultsToEmpty()
    {
        const string json = """
            {"ModelNumber":"RZ09-0484","EvidenceSource":"https://example.invalid/source","Detected":true,"WritesPaused":false,"Observations":[]}
            """;
        DiagnosticExperimentalSupport? support = JsonSerializer.Deserialize<DiagnosticExperimentalSupport>(json);
        Assert.NotNull(support);
        Assert.Empty(support.FeatureEvidence);
        Assert.Equal("UpstreamDerived", support.EvidenceLevel);
    }

    [Fact]
    public void FeatureEvidence_RoundTripsWithoutChangingWritePauseOrHistory()
    {
        var support = new DiagnosticExperimentalSupport("RZ09-0484", "pinned-source", true, true, [])
        {
            FeatureEvidence = [new("UniformStaticRgb", "SourceBackedExtrapolationWithoutRgbReadback", ["pinned-frame-source"])],
        };
        var decoded = JsonSerializer.Deserialize<DiagnosticExperimentalSupport>(JsonSerializer.Serialize(support));
        Assert.NotNull(decoded);
        Assert.True(decoded.WritesPaused);
        Assert.Empty(decoded.Observations);
        ExperimentalFeatureEvidence evidence = Assert.Single(decoded.FeatureEvidence);
        Assert.Equal("SourceBackedExtrapolationWithoutRgbReadback", evidence.Basis);
        Assert.Equal("pinned-frame-source", Assert.Single(evidence.Sources));
    }
}
