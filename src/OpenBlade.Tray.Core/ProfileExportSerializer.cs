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
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase,
                    allowIntegerValues: false),
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

    public static ProfileSet Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The profile file is empty.");
        }

        int schemaVersion = ReadSchemaVersion(json);
        return schemaVersion switch
        {
            1 => DeserializeVersion1(json),
            > CurrentSchemaVersion => throw new InvalidDataException(
                $"Profile schema {schemaVersion} was created by a newer version of OpenBlade."),
            _ => throw new InvalidDataException(
                $"Profile schema {schemaVersion} is not supported by this version of OpenBlade."),
        };
    }

    private static int ReadSchemaVersion(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "schemaVersion",
                    out JsonElement schemaVersionElement)
                || schemaVersionElement.ValueKind != JsonValueKind.Number
                || !schemaVersionElement.TryGetInt32(out int schemaVersion))
            {
                throw new InvalidDataException(
                    "The profile file does not contain a valid schema version.");
            }

            return schemaVersion;
        }
        catch (JsonException exception)
        {
            throw InvalidDocument(exception);
        }
    }

    // Keep each historical reader isolated. A future schema can deserialize into
    // its own DTO and migrate to ProfileSet before returning from Deserialize.
    private static ProfileSet DeserializeVersion1(string json)
    {
        ProfileExportDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileExportDocument>(
                json,
                SerializerOptions)
                ?? throw new InvalidDataException("The profile file is empty.");
        }
        catch (JsonException exception)
        {
            throw InvalidDocument(exception);
        }

        if (document.Profiles is null)
        {
            throw new InvalidDataException("The profile file does not contain profiles.");
        }

        return document.Profiles;
    }

    private static InvalidDataException InvalidDocument(Exception exception) =>
        new(
            "The selected file is not a valid OpenBlade profile export.",
            exception);
}
