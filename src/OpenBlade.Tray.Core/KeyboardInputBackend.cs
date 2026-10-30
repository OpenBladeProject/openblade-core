using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Tray;

[JsonConverter(typeof(KeyboardInputBackendJsonConverter))]
public enum KeyboardInputBackend
{
    Unknown = 0,
    WindowsHook = 1,
    RazerDriver = 2,
}

public static class KeyboardInputBackendExtensions
{
    public static KeyboardInputBackend Normalize(this KeyboardInputBackend value) =>
        Enum.IsDefined(value) ? value : KeyboardInputBackend.Unknown;
}

/// <summary>Future selections remain unavailable instead of choosing another input owner.</summary>
public sealed class KeyboardInputBackendJsonConverter : JsonConverter<KeyboardInputBackend>
{
    public override KeyboardInputBackend Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => Enum.TryParse(reader.GetString(), ignoreCase: true,
                out KeyboardInputBackend value) ? value.Normalize() : KeyboardInputBackend.Unknown,
            JsonTokenType.Number => reader.TryGetInt32(out int value)
                ? ((KeyboardInputBackend)value).Normalize() : KeyboardInputBackend.Unknown,
            JsonTokenType.Null => KeyboardInputBackend.Unknown,
            _ => throw new JsonException("Keyboard input backend must be a string or number."),
        };

    public override void Write(Utf8JsonWriter writer, KeyboardInputBackend value,
        JsonSerializerOptions options) => writer.WriteStringValue(value.Normalize().ToString());
}
