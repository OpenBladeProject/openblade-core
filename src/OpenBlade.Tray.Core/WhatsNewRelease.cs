using System.Text.RegularExpressions;

namespace OpenBlade.Tray;

public sealed record WhatsNewSection(string Heading, IReadOnlyList<string> Items);

/// <summary>Offline release notes from the changelog shipped with this build.</summary>
public sealed class WhatsNewRelease
{
    public string Version { get; }
    public IReadOnlyList<WhatsNewSection> Sections { get; }

    public WhatsNewRelease(string productVersion, string changelog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentNullException.ThrowIfNull(changelog);
        // Commit metadata changes on rebuilds; nightly revisions remain distinct releases.
        Version = productVersion.Split('+', 2)[0].Trim();
        Sections = ReadSections(changelog, Version);
    }

    public bool ShouldShow(string? lastSeenVersion) =>
        !string.Equals(Version, lastSeenVersion, StringComparison.Ordinal);

    public static WhatsNewRelease Load(string productVersion)
    {
        using Stream stream = typeof(WhatsNewRelease).Assembly
            .GetManifestResourceStream("OpenBlade.Changelog.md")
            ?? throw new InvalidOperationException("The bundled changelog is missing.");
        using var reader = new StreamReader(stream);
        return new WhatsNewRelease(productVersion, reader.ReadToEnd());
    }

    private static IReadOnlyList<WhatsNewSection> ReadSections(string changelog, string version)
    {
        // A published build can have its own dated section. Development builds use
        // Unreleased; never substitute another version's notes when it is empty.
        string[] lines = changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string exactHeading = $"## [{version}]";
        int start = Array.FindIndex(lines, line =>
            line.Equals(exactHeading, StringComparison.Ordinal)
            || line.StartsWith(exactHeading + " - ", StringComparison.Ordinal));
        if (start < 0)
        {
            start = Array.FindIndex(lines, line => line.Trim() == "## [Unreleased]");
        }
        if (start < 0)
        {
            return [];
        }

        var sections = new List<WhatsNewSection>();
        string heading = string.Empty;
        var items = new List<string>();
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                AddSection();
                heading = line[4..].Trim();
                items = [];
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(PlainText(line[2..]));
            }
            else if (!string.IsNullOrWhiteSpace(line)
                && char.IsWhiteSpace(line[0])
                && items.Count > 0)
            {
                items[^1] += " " + PlainText(line.Trim());
            }
        }
        AddSection();
        return sections;

        void AddSection()
        {
            if (items.Count > 0)
            {
                sections.Add(new WhatsNewSection(heading, items.ToArray()));
            }
        }
    }

    private static string PlainText(string markdown)
    {
        // The in-app summary omits PR footnotes; the full changelog retains them.
        string text = Regex.Replace(markdown, @"\s*\(\[#\d+\]\([^)]+\)\)", string.Empty);
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal).Trim();
    }
}
