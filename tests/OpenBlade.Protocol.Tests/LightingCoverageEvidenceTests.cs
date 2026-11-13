using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class LightingCoverageEvidenceTests
{
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
