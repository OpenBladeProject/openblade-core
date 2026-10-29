using System.Text.RegularExpressions;

namespace OpenBlade.Tray;

public sealed record WhatsNewSection(string Heading, IReadOnlyList<string> Items);

/// <summary>Offline release notes from the changelog shipped with this build.</summary>
public sealed class WhatsNewRelease
{
    public string Version { get; }
    private readonly Lazy<IReadOnlyList<WhatsNewSection>> sections;

    public IReadOnlyList<WhatsNewSection> Sections => sections.Value;

    public WhatsNewRelease(string productVersion, string changelog)
        : this(productVersion, () => changelog)
    {
        ArgumentNullException.ThrowIfNull(changelog);
    }

    private WhatsNewRelease(string productVersion, Func<string> readChangelog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        // Commit metadata changes on rebuilds; nightly revisions remain distinct releases.
        Version = productVersion.Split('+', 2)[0].Trim();
        sections = new Lazy<IReadOnlyList<WhatsNewSection>>(
            () => ReadSections(readChangelog(), Version));
    }

    public bool ShouldShow(string? lastSeenVersion) =>
        !string.Equals(Version, lastSeenVersion, StringComparison.Ordinal);

    public static WhatsNewRelease Load(string productVersion) =>
        new(productVersion, ReadBundledChangelog);

    private static string ReadBundledChangelog()
    {
        using Stream stream = typeof(WhatsNewRelease).Assembly
            .GetManifestResourceStream("OpenBlade.Changelog.md")
            ?? throw new InvalidOperationException("The bundled changelog is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<WhatsNewSection> ReadSections(string changelog, string version)
    {
        // A published build can have its own dated section. Development builds use
        // Unreleased; never substitute another version's notes when it is empty.
        string[] lines = changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string exactHeading = $"## [{version}]";
        int start = Array.FindIndex(lines, line =>
            line.Trim().Equals(exactHeading, StringComparison.Ordinal)
            || line.Trim().StartsWith(exactHeading + " - ", StringComparison.Ordinal));
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
        bool fencedCode = false;
        bool paragraphBreak = false;
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fencedCode = !fencedCode;
                continue;
            }
            if (fencedCode)
            {
                if (items.Count > 0)
                {
                    items[^1] += "\n" + line.TrimEnd();
                }
                continue;
            }
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
            else if (trimmed.Length == 0)
            {
                paragraphBreak = true;
                continue;
            }
            else if (items.Count > 0)
            {
                string separator = paragraphBreak ? "\n\n" : " ";
                string continuation = trimmed;
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    separator = "\n";
                    continuation = "• " + trimmed[2..];
                }
                items[^1] += separator + PlainText(continuation);
            }
            paragraphBreak = false;
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
        string text = Regex.Replace(markdown, @"\s*\((?:\[[^\]]*#\d+\]\([^)]+\)(?:,\s*)?)+\)", string.Empty);
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal).Trim();
    }
}
