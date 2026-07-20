using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record ProfileExportDocument(
    int SchemaVersion,
    string ApplicationVersion,
    DateTimeOffset ExportedAtUtc,
    ProfileSet Profiles);

public static class ProfileExportSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectRequiredConstructorParameters = true,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            },
        };

    public static string Serialize(
        ProfileSet profiles,
        string applicationVersion,
        DateTimeOffset exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (string.IsNullOrWhiteSpace(applicationVersion))
        {
            throw new ArgumentException(
                "An application version is required.",
                nameof(applicationVersion));
        }

        return JsonSerializer.Serialize(
            new ProfileExportDocument(
                CurrentSchemaVersion,
                applicationVersion,
                exportedAtUtc.ToUniversalTime(),
                profiles),
            SerializerOptions);
    }
}
