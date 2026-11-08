using System.Text.Json;

namespace OpenBlade.Testing;

internal static class ParityFixture
{
    internal static IEnumerable<object[]> Rows(string file, string suite, string section)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Parity", file);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(suite, root.GetProperty("suite").GetString());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in root.GetProperty(section).EnumerateArray())
        {
            string id = row.GetProperty("id").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(ids.Add(id), $"Duplicate fixture ID: {id}");
            yield return [id, row.GetRawText()];
        }
        Assert.NotEmpty(ids);
    }
}
