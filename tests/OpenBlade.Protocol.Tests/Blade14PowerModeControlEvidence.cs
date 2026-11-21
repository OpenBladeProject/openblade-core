using System.Text.Json;

namespace OpenBlade.Protocol.Tests;

// Reads the RZ09-0482 Power Mode Control evidence record so catalog tests
// compare against the reviewed pins instead of a duplicated literal list.
internal static class Blade14PowerModeControlEvidence
{
    internal const string RelativePath = "evidence/source-reviewed-blades/2026-11-19-rz09-0482-maximum-fans.json";

    internal static JsonDocument Load()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, RelativePath)))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, RelativePath)));
    }

    // Expected catalog basis and sources: each listed source as url;SHA256=hash,
    // then the evidence record itself. Every listed source must be verified.
    internal static (string Basis, string[] Sources) CatalogFeature(string feature)
    {
        using JsonDocument document = Load();
        JsonElement root = document.RootElement;
        JsonElement entry = root.GetProperty("catalogFeatures").GetProperty(feature);
        Dictionary<string, JsonElement> sources = root.GetProperty("sources").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!);
        var expected = new List<string>();
        foreach (JsonElement id in entry.GetProperty("sources").EnumerateArray())
        {
            JsonElement source = sources[id.GetString()!];
            Assert.True(source.GetProperty("verifiedThisReview").GetBoolean());
            expected.Add($"{source.GetProperty("url").GetString()};SHA256={source.GetProperty("sha256").GetString()}");
        }
        expected.Add(RelativePath);
        return (entry.GetProperty("basis").GetString()!, expected.ToArray());
    }
}
