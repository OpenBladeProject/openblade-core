using System.Text.Json;

namespace OpenBlade.DeploymentClient;

internal enum DeploymentBrokerAction
{
    ApplyFixedInbox,
}

internal sealed record DeploymentBrokerRequest(
    int ProtocolVersion,
    Guid RequestId,
    DeploymentBrokerAction Action);

internal enum DeploymentBrokerResponseStatus
{
    Rejected,
    Succeeded,
    RolledBack,
    Indeterminate,
}

internal sealed record DeploymentBrokerResponse(
    int ProtocolVersion,
    Guid RequestId,
    DeploymentBrokerResponseStatus Status,
    string Code);

internal static class DeploymentBrokerProtocol
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumFrameBytes = 512;
    internal const string SuccessCode = "install-succeeded";

    private static readonly string[] RequestProperties =
    [
        "protocolVersion",
        "requestId",
        "action",
    ];

    private static readonly string[] ResponseProperties =
    [
        "protocolVersion",
        "requestId",
        "status",
        "code",
    ];

    internal static byte[] SerializeRequest(DeploymentBrokerRequest request)
    {
        ValidateRequest(request);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", request.ProtocolVersion);
            writer.WriteString("requestId", request.RequestId.ToString("D"));
            writer.WriteString("action", nameof(DeploymentBrokerAction.ApplyFixedInbox));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    internal static DeploymentBrokerRequest ParseRequest(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document = ParseDocument(bytes, "request");
        using (document)
        {
            IReadOnlyDictionary<string, JsonElement> properties = ReadProperties(
                document.RootElement,
                RequestProperties,
                "request");
            string action = ReadString(properties, "action", "request");
            DeploymentBrokerRequest request = new(
                ReadInt32(properties, "protocolVersion", "request"),
                ReadGuid(properties, "requestId", "request"),
                string.Equals(
                    action,
                    nameof(DeploymentBrokerAction.ApplyFixedInbox),
                    StringComparison.Ordinal)
                    ? DeploymentBrokerAction.ApplyFixedInbox
                    : throw new InvalidDataException("The broker request action is invalid."));
            ValidateRequest(request);
            if (!bytes.SequenceEqual(SerializeRequest(request)))
            {
                throw new InvalidDataException("The broker request is not canonical JSON.");
            }

            return request;
        }
    }

    internal static byte[] SerializeResponse(DeploymentBrokerResponse response)
    {
        ValidateResponse(response);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", response.ProtocolVersion);
            writer.WriteString("requestId", response.RequestId.ToString("D"));
            writer.WriteString("status", response.Status.ToString());
            writer.WriteString("code", response.Code);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    internal static DeploymentBrokerResponse ParseResponse(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document = ParseDocument(bytes, "response");
        using (document)
        {
            IReadOnlyDictionary<string, JsonElement> properties = ReadProperties(
                document.RootElement,
                ResponseProperties,
                "response");
            string status = ReadString(properties, "status", "response");
            if (!Enum.TryParse(status, ignoreCase: false, out DeploymentBrokerResponseStatus parsedStatus)
                || !Enum.IsDefined(parsedStatus))
            {
                throw new InvalidDataException("The broker response status is invalid.");
            }

            DeploymentBrokerResponse response = new(
                ReadInt32(properties, "protocolVersion", "response"),
                ReadGuid(properties, "requestId", "response"),
                parsedStatus,
                ReadString(properties, "code", "response"));
            ValidateResponse(response);
            if (!bytes.SequenceEqual(SerializeResponse(response)))
            {
                throw new InvalidDataException("The broker response is not canonical JSON.");
            }

            return response;
        }
    }

    internal static bool IsExactSuccess(DeploymentBrokerResponse response, Guid requestId) =>
        response.ProtocolVersion == ProtocolVersion
        && response.RequestId == requestId
        && response.Status == DeploymentBrokerResponseStatus.Succeeded
        && string.Equals(response.Code, SuccessCode, StringComparison.Ordinal);

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> bytes, string description)
    {
        if (bytes.IsEmpty
            || bytes.Length > MaximumFrameBytes
            || (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })))
        {
            throw new InvalidDataException($"The broker {description} has an invalid encoding or size.");
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 2,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException($"The broker {description} must be a JSON object.");
            }

            return document;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The broker {description} is malformed.", exception);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadProperties(
        JsonElement root,
        IReadOnlyCollection<string> required,
        string description)
    {
        HashSet<string> allowed = new(required, StringComparer.Ordinal);
        HashSet<string> exact = new(StringComparer.Ordinal);
        HashSet<string> insensitive = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !exact.Add(property.Name)
                || !insensitive.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The broker {description} has an unknown or duplicate property.");
            }

            properties.Add(property.Name, property.Value);
        }

        if (properties.Count != required.Count
            || required.Any(name => !properties.ContainsKey(name)))
        {
            throw new InvalidDataException($"The broker {description} is incomplete.");
        }

        return properties;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        string description) => properties[name].ValueKind == JsonValueKind.String
        ? properties[name].GetString()!
        : throw new InvalidDataException($"Broker {description} property '{name}' must be a string.");

    private static int ReadInt32(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        string description) => properties[name].TryGetInt32(out int result)
        ? result
        : throw new InvalidDataException($"Broker {description} property '{name}' must be an integer.");

    private static Guid ReadGuid(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        string description)
    {
        string text = ReadString(properties, name, description);
        return Guid.TryParseExact(text, "D", out Guid result)
            ? result
            : throw new InvalidDataException(
                $"Broker {description} property '{name}' must be a canonical GUID.");
    }

    private static void ValidateRequest(DeploymentBrokerRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion
            || request.RequestId == Guid.Empty
            || request.Action != DeploymentBrokerAction.ApplyFixedInbox)
        {
            throw new InvalidDataException("The broker request contract is invalid.");
        }
    }

    private static void ValidateResponse(DeploymentBrokerResponse response)
    {
        if (response.ProtocolVersion != ProtocolVersion
            || response.RequestId == Guid.Empty
            || !Enum.IsDefined(response.Status)
            || response.Code.Length is < 1 or > 64
            || response.Code[0] is < 'a' or > 'z'
            || response.Code[^1] is < 'a' or > 'z'
            || response.Code.Any(character => character is not (>= 'a' and <= 'z') and not '-'))
        {
            throw new InvalidDataException("The broker response contract is invalid.");
        }
    }
}
