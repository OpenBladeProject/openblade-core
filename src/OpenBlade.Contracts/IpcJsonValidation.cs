using System.Text.Json;

namespace OpenBlade.Contracts;

public static class IpcJsonValidation
{
    // Validate the complete payload before version checks or custom converters run.
    // A streaming pass avoids materializing a second representation of every frame.
    public static void ValidateNoDuplicateMembers(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload);
        if (!reader.Read())
        {
            throw new JsonException("IPC message is empty.");
        }
        ValidateValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("IPC message contains more than one value.");
        }
    }

    private static void ValidateValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return;
                }
                if (!names.Add(reader.GetString()!))
                {
                    throw new JsonException("IPC message contains a duplicate member.");
                }
                reader.Read();
                ValidateValue(ref reader);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return;
                }
                ValidateValue(ref reader);
            }
        }
    }
}
