using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class LightingCoverageEvidenceTests
{
    [Fact]
    public void CompleteLightingPassCoversEveryModelAndMatchesAdmittedEffectsAndPinnedProofs()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        const string relativePath = "evidence/source-reviewed-blades/2026-11-13-complete-lighting-pass.json";
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, relativePath)));
        JsonElement root = document.RootElement;
        JsonElement[] models = root.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(RazerBladeModelCatalog.All.Select(model => model.ModelNumber).Order(StringComparer.Ordinal),
            models.Select(model => model.GetProperty("model").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(models, model => Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("primaryEvidence").GetString())));
        foreach (ExperimentalBladeModel profile in ExperimentalBladeModels.All)
        {
            JsonElement model = Assert.Single(models, model => model.GetProperty("model").GetString() == profile.ModelNumber);
            Assert.Equal(ExperimentalBladePort.CreateCapabilities(profile, true).LightingEffects.Select(effect => effect.ToString()).Order(StringComparer.Ordinal),
                model.GetProperty("writableEffects").EnumerateArray().Select(effect => effect.GetString()!).Order(StringComparer.Ordinal));
            Assert.Equal(model.TryGetProperty("independentFieldControls", out JsonElement fields) && fields.GetBoolean(),
                ExperimentalBladePort.CreateCapabilities(profile, true).SupportsIndependentLightingControls);
        }
        foreach (JsonElement proof in root.GetProperty("proofs").EnumerateArray())
        {
            string file = Path.Combine(directory.FullName, "evidence", "source-reviewed-blades", proof.GetProperty("path").GetString()!);
            Assert.Equal(proof.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        }
        Assert.False(root.GetProperty("physicalValidationPerformed").GetBoolean());
    }

    [Fact]
    public void LightingReviewAccountsForEverySupportedProfileExactlyOnce()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        const string relativePath = "evidence/source-reviewed-blades/2026-11-13-lighting-coverage.json";
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, relativePath)));
        JsonElement root = document.RootElement;
        string[] models = new[] { "sourceReviewedModels", "remainingSourceReviewedModels", "existingProfiles" }
            .SelectMany(section => root.GetProperty(section).EnumerateArray()
                .Select(model => model.GetProperty("model").GetString()!)).ToArray();
        Assert.Equal(models.Length, models.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(RazerBladeModelCatalog.All.Select(model => model.ModelNumber).Order(StringComparer.Ordinal),
            models.Order(StringComparer.Ordinal));
        foreach (JsonElement model in root.GetProperty("sourceReviewedModels").EnumerateArray())
        {
            Assert.True(model.GetProperty("keyboardBrightnessQuery").GetBoolean());
            Assert.True(model.GetProperty("keyboardBrightnessSetter").GetBoolean());
            Assert.False(model.GetProperty("quickEffectsEstablishMatrixReadback").GetBoolean());
            foreach (JsonElement source in model.GetProperty("sources").EnumerateArray())
            {
                Assert.StartsWith("https://apps.razer.com/synapse/products/", source.GetProperty("url").GetString());
                Assert.Matches("^[0-9A-F]{64}$", source.GetProperty("sha256").GetString()!);
            }
        }
    }
}
