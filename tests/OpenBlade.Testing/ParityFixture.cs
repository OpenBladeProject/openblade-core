using System.Text.Json;

namespace OpenBlade.Testing;

internal static class ParityFixture
{
    internal const string SourceRevision = "123936f3ea69881d21bd16a7a09529930c9a2c6f";

    internal static IEnumerable<object[]> Rows(string file, string suite, string section) =>
        Read(file, suite, section).Select(row => new object[] { row.GetProperty("id").GetString()! });

    internal static JsonElement Row(string file, string suite, string section, string id) =>
        Read(file, suite, section).Single(row => row.GetProperty("id").GetString() == id);

    private static IReadOnlyList<JsonElement> Read(string file, string suite, string section) =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", file)), suite, section);

    internal static IReadOnlyList<JsonElement> Parse(string json, string suite, string section)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        JsonElement root = document.RootElement;
        bool report = suite == "razer-report";
        Assert.True(report || suite == "application-profile-merge", $"Unknown fixture suite: {suite}");
        Properties(root, report
            ? ["schemaVersion", "suite", "sourceRevision", "encoding", "responses"]
            : ["schemaVersion", "suite", "sourceRevision", "cases"]);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(suite, root.GetProperty("suite").GetString());
        Assert.Equal(SourceRevision, root.GetProperty("sourceRevision").GetString());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<JsonElement>();
        foreach (JsonElement row in root.GetProperty(section).EnumerateArray())
        {
            Properties(row, !report
                ? ["id", "baseline", "override", "expected", "expectedError"]
                : section == "encoding"
                    ? ["id", "request", "destinationLength", "expectedError", "expectedReportHex"]
                    : ["id", "request", "responseHex", "expectedPayloadLength", "expectedFailure", "expectedPayloadHex"]);
            if (report)
            {
                Properties(row.GetProperty("request"), "transactionId", "commandClass", "commandId", "payloadHex");
                foreach (JsonProperty property in row.EnumerateObject().Concat(row.GetProperty("request").EnumerateObject()))
                {
                    if (property.Name.EndsWith("Hex", StringComparison.Ordinal) && property.Value.ValueKind != JsonValueKind.Null)
                    {
                        Hex(property.Value);
                    }
                }
                if (section == "responses" && row.GetProperty("expectedFailure").ValueKind != JsonValueKind.Null)
                {
                    Properties(row.GetProperty("expectedFailure"), "name", "value");
                }
            }
            string id = row.GetProperty("id").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(ids.Add(id), $"Duplicate fixture ID: {id}");
            rows.Add(row.Clone());
        }
        Assert.NotEmpty(ids);
        return rows;
    }

    internal static byte[] Hex(JsonElement value)
    {
        string hex = value.GetString()!;
        Assert.True(hex.Length % 2 == 0 && hex.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'),
            $"Expected uppercase full-byte hex: {hex}");
        return Convert.FromHexString(hex);
    }

    private static void Properties(JsonElement value, params string[] names)
    {
        string[] actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.True(actual.Length == names.Length && names.ToHashSet(StringComparer.Ordinal).SetEquals(actual),
            $"Expected fields [{string.Join(", ", names)}]; got [{string.Join(", ", actual)}]");
    }
}
