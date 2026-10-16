using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalEvidenceCatalogTests
{
    [Fact]
    public void ExactModelSnapshots_RetainPerFeaturePinnedSources()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            var snapshot = new ExperimentalFeaturesPolicy(model).Snapshot();
            Assert.Equal(model.ModelNumber, snapshot.ModelNumber);
            Assert.NotEmpty(snapshot.FeatureEvidence);
            Assert.All(snapshot.FeatureEvidence, feature =>
            {
                Assert.NotEmpty(feature.Sources);
                if (feature.Feature == "AdapterPowerClassification")
                {
                    Assert.Equal("SynapseSourceBacked", feature.Basis);
                    Assert.Equal(model.AdapterPowerEvidenceSource, Assert.Single(feature.Sources));
                    Assert.StartsWith("https://apps.razer.com/synapse/products/", model.AdapterPowerEvidenceSource);
                }
                else if (feature.Feature is "HyperBoost" or "PadlessHyperBoost")
                {
                    bool blade14 = model.ProductId == 0x029D;
                    Assert.True(blade14 || model.ProductId == 0x029F);
                    string basis = feature.Feature == "HyperBoost" ? "SynapseSourceBacked" : "SourceBackedExtrapolation";
                    Assert.Equal(blade14 ? basis + "NotPubliclyListed" : basis, feature.Basis);
                    Assert.All(feature.Sources, source => Assert.StartsWith(
                        $"https://apps.razer.com/synapse/products/{(blade14 ? 669 : 671)}/mw/", source));
                }
                else
                {
                    Assert.All(feature.Sources, source => Assert.StartsWith("https://github.com/", source));
                }
            });
            Assert.Equal(snapshot.FeatureEvidence.Count, snapshot.FeatureEvidence.Select(item => item.Feature).Distinct().Count());
        }
    }

    [Fact]
    public void Blade18UniformStatic_DoesNotClaimRgbReadbackOrOwnerValidation()
    {
        var model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var evidence = Assert.Single(new ExperimentalFeaturesPolicy(model).Snapshot().FeatureEvidence,
            item => item.Feature == "UniformStaticRgb");
        Assert.Equal("SourceBackedExtrapolationWithoutRgbReadback", evidence.Basis);
    }

    [Fact]
    public void Blade15Evidence_DoesNotAddUnsupportedBatteryOrLogoClaims()
    {
        var model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x0255);
        var evidence = new ExperimentalFeaturesPolicy(model).Snapshot().FeatureEvidence;
        Assert.DoesNotContain(evidence, item => item.Feature.Contains("Charge", StringComparison.Ordinal)
            || item.Feature.Contains("Logo", StringComparison.Ordinal));
    }
}
