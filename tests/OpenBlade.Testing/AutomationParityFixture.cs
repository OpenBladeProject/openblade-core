using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Testing;

internal static class AutomationParityFixture
{
    internal const string Revision = "9e53ed92bbe3985e1ea899ed6c1124f6f6dd020e";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal static IReadOnlyList<AutomationCase> Read() => Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Parity", "automation-policy.json")));

    internal static IReadOnlyList<AutomationCase> Parse(string json)
    {
        Fixture fixture = JsonSerializer.Deserialize<Fixture>(json, Options)
            ?? throw new JsonException("Missing fixture.");
        if (fixture.SchemaVersion != 1 || fixture.Suite != "automation-selection"
            || fixture.SourceRevision != Revision || fixture.Cases is null || fixture.Cases.Count != 18)
        {
            throw new JsonException("Invalid automation fixture provenance/count.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (AutomationCase row in fixture.Cases)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id)
                || row.Operation > 1 || row.Origin > 3 || row.Provenance > 4)
            {
                throw new JsonException("Invalid automation case.");
            }
        }
        return fixture.Cases;
    }

    private sealed record Fixture
    {
        public required int SchemaVersion { get; init; }
        public required string Suite { get; init; }
        public required string SourceRevision { get; init; }
        public required List<AutomationCase> Cases { get; init; }
    }
}

internal sealed record AutomationCase
{
    public required string Id { get; init; }
    public required uint Operation { get; init; }
    public required bool Profile { get; init; }
    public required bool Confirmed { get; init; }
    public required bool Temporary { get; init; }
    public required bool Persistent { get; init; }
    public required bool ExternalUnknown { get; init; }
    public required bool Application { get; init; }
    public required uint Origin { get; init; }
    public required uint Provenance { get; init; }
}
