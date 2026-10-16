namespace OpenBlade.Tray;

public enum AppAccentColorMode
{
    RazerGreen,
    Windows,
    Custom,
}

public sealed record AppAccentColorOption(
    AppAccentColorMode Mode,
    string Label);

public static class AppAccentColorPreferenceDefaults
{
    public const string RazerGreenHex = "#47E10C";

    public static string NormalizeCustomColor(string? value) =>
        TryNormalizeHexColor(value, out string normalized)
            ? normalized
            : RazerGreenHex;

    public static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();
        if (candidate.StartsWith('#'))
        {
            candidate = candidate[1..];
        }

        if (candidate.Length != 6 || !candidate.All(Uri.IsHexDigit))
        {
            return false;
        }

        normalized = $"#{candidate.ToUpperInvariant()}";
        return true;
    }
}

public enum PrimaryNavigationItemKind
{
    Profiles = 0,
    Laptop = 1,
    Lighting = 2,
    Display = 3,
    Settings = 4,
    ThxAudio = 5,
}

public sealed class PrimaryNavigationItemOption
{
    public PrimaryNavigationItemOption(PrimaryNavigationItemKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public PrimaryNavigationItemKind Kind { get; }

    public string Label { get; }
}

public static class PrimaryNavigationPreferenceDefaults
{
    private static IReadOnlyList<PrimaryNavigationItemKind> LegacyItems { get; } =
    [
        PrimaryNavigationItemKind.Profiles,
        PrimaryNavigationItemKind.Laptop,
        PrimaryNavigationItemKind.Lighting,
        PrimaryNavigationItemKind.Display,
        PrimaryNavigationItemKind.Settings,
    ];

    private static IReadOnlyList<PrimaryNavigationItemKind> PreviousItems { get; } =
    [
        PrimaryNavigationItemKind.Profiles,
        PrimaryNavigationItemKind.Display,
        PrimaryNavigationItemKind.Laptop,
        PrimaryNavigationItemKind.Lighting,
        PrimaryNavigationItemKind.Settings,
    ];

    public static IReadOnlyList<PrimaryNavigationItemKind> Items { get; } =
    [
        PrimaryNavigationItemKind.Profiles,
        PrimaryNavigationItemKind.Display,
        PrimaryNavigationItemKind.Laptop,
        PrimaryNavigationItemKind.ThxAudio,
        PrimaryNavigationItemKind.Lighting,
        PrimaryNavigationItemKind.Settings,
    ];

    public static IReadOnlyList<PrimaryNavigationItemKind> Normalize(
        IReadOnlyList<PrimaryNavigationItemKind>? preferences)
    {
        if (preferences is null)
        {
            return Items;
        }

        // Move installations that still use previous defaults without
        // replacing a navigation order the user customized.
        if (preferences.SequenceEqual(LegacyItems) || preferences.SequenceEqual(PreviousItems))
        {
            return Items;
        }

        var seen = new HashSet<PrimaryNavigationItemKind>();
        var normalized = new List<PrimaryNavigationItemKind>(Items.Count);
        foreach (PrimaryNavigationItemKind item in preferences)
        {
            if (Enum.IsDefined(item) && seen.Add(item))
            {
                normalized.Add(item);
            }
        }

        foreach (PrimaryNavigationItemKind item in Items)
        {
            if (seen.Add(item))
            {
                normalized.Add(item);
            }
        }

        return normalized;
    }
}
