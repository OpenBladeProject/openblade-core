using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenBlade.Tray.Tests;

// Layout contracts inspect English text; localization contracts inspect the untouched source.
// Keep resource identity as an XML annotation rather than recreating retired x:Uid attributes.
internal static class CanonicalLocalizedXaml
{
    private sealed record ResourceIdentity(string Value);

    internal static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    internal static string Read(string fileName) => Resolve(File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "OpenBlade.Tray", fileName)));

    internal static XDocument Load(string fileName)
    {
        XDocument document = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "OpenBlade.Tray", fileName));
        IReadOnlyDictionary<string, string> catalog = ReadCatalog();
        foreach (XElement element in document.Descendants())
        {
            foreach (XAttribute attribute in element.Attributes().ToArray())
            {
                if (!attribute.Value.StartsWith("{CustomResource ", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = Key(attribute.Value);
                string identity = key[..key.IndexOf('.')];
                ResourceIdentity? existing = element.Annotation<ResourceIdentity>();
                if (existing is not null && existing.Value != identity)
                {
                    throw new InvalidDataException($"Mixed localized resource identities on {element.Name}: {key}");
                }

                if (existing is null)
                {
                    element.AddAnnotation(new ResourceIdentity(identity));
                }

                attribute.Value = catalog[key];
            }
        }

        return document;
    }

    internal static string? Identity(XElement element) => element.Annotation<ResourceIdentity>()?.Value;

    internal static IReadOnlyDictionary<string, string> ReadCatalog(string culture = "en-US") =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "src", "OpenBlade.Tray", "Strings", culture,
                "Resources.resw")).Root!.Elements("data").ToDictionary(
            entry => (string)entry.Attribute("name")!, entry => entry.Element("value")!.Value,
            StringComparer.Ordinal);

    internal static string Resolve(string xaml)
    {
        IReadOnlyDictionary<string, string> catalog = ReadCatalog();
        return Regex.Replace(xaml, @"\{CustomResource ([^{}]+)\}", match =>
        {
            string value = catalog[Key(match.Value)];
            // Preserve original source formatting for the existing layout contracts.
            return value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\r", "&#xD;", StringComparison.Ordinal)
                .Replace("\n", "&#xA;", StringComparison.Ordinal);
        });
    }

    internal static string Key(string markup)
    {
        Match match = Regex.Match(markup, @"^\{CustomResource ([^{}\s]+)\}$");
        if (!match.Success || !match.Groups[1].Value.Contains('.'))
        {
            throw new InvalidDataException($"Invalid localized XAML resource: {markup}");
        }

        return match.Groups[1].Value;
    }
}
