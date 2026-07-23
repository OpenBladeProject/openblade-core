namespace OpenBlade.Tray;

public static class SpecialKeyShortcutParser
{
    private static readonly IReadOnlyDictionary<string, ushort> NamedKeys =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Backspace"] = 0x08,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Space"] = 0x20,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
        };

    public static (ushort Key, ushort[] Modifiers) Parse(string shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        string[] tokens = shortcut.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Length > 5)
        {
            throw new FormatException("Use a shortcut such as Ctrl+Shift+M.");
        }

        var modifiers = new List<ushort>(4);
        for (int index = 0; index < tokens.Length - 1; index++)
        {
            ushort modifier = tokens[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "SHIFT" => 0x10,
                "ALT" => 0x12,
                "WIN" or "WINDOWS" => 0x5B,
                _ => throw new FormatException($"Unknown modifier: {tokens[index]}"),
            };
            if (!modifiers.Contains(modifier))
            {
                modifiers.Add(modifier);
            }
        }

        ushort key = ParseKey(tokens[^1]);
        if (modifiers.Contains(key))
        {
            throw new FormatException("A shortcut must include a non-modifier key.");
        }
        return (key, modifiers.ToArray());
    }

    public static bool TryParse(string? shortcut, out string validationMessage)
    {
        try
        {
            _ = Parse(shortcut ?? string.Empty);
            validationMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            validationMessage = exception.Message;
            return false;
        }
    }

    private static ushort ParseKey(string token)
    {
        if (token.Length == 1)
        {
            char value = char.ToUpperInvariant(token[0]);
            if (value is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return value;
            }
        }
        if (token.Length is 2 or 3
            && token[0] is 'F' or 'f'
            && int.TryParse(token[1..], out int function)
            && function is >= 1 and <= 24)
        {
            return checked((ushort)(0x6F + function));
        }
        if (NamedKeys.TryGetValue(token.Replace(" ", string.Empty), out ushort key))
        {
            return key;
        }

        throw new FormatException($"Unknown shortcut key: {token}");
    }
}
